using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Threading;
using RemoteControl.Net.Peering;
using RemoteControl.Protocol;
using RemoteControl.Session;
using RemoteControl.Signaling;
using RemoteControl.SignalingServer;

namespace RemoteControl.App;

public partial class MainWindow : Window
{
    private const int SignalingPort = 7777;
    private const string PairingCodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no 0/O/1/I -- easy to read aloud/type.

    private readonly UiLogger _logger = new();
    private readonly ObservableCollection<string> _logLines = [];
    private readonly DispatcherTimer _uiTimer;

    private CancellationTokenSource? _cts;
    private Thread? _sessionThread;
    private SignalingServerHost? _signalingHost;
    private string _peerDescription = "";
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        LogListBox.ItemsSource = _logLines;
        Closing += MainWindow_Closing;

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _uiTimer.Tick += (_, _) =>
        {
            _logger.DrainTo(_logLines, maxLines: 500);
            if (_logLines.Count > 0)
                LogListBox.ScrollIntoView(_logLines[^1]);
        };
        _uiTimer.Start();
    }

    private void LogListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (LogListBox.SelectedItems.Count == 0) return;
        var text = string.Join(Environment.NewLine, LogListBox.SelectedItems.Cast<string>());
        try
        {
            Clipboard.SetText(text);
        }
        catch (COMException)
        {
            // Another app briefly held the clipboard -- not worth bothering the user about.
        }
    }

    private void ModeRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (HostPanel is null || ClientPanel is null) return; // fires once during InitializeComponent, before both exist.
        HostPanel.Visibility = HostModeRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ClientPanel.Visibility = ClientModeRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void StartHostButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true);

        var pairingCode = GeneratePairingCode();
        PairingCodeDisplay.Text = pairingCode;

        var host = StartSignalingServer();
        if (host is null)
        {
            SetStatus("Failed to start (see log).");
            SetBusy(false);
            return;
        }

        _signalingHost = host;
        var displayAddress = GetPrimaryLanAddress();
        LocalAddressesDisplay.Text = displayAddress is not null
            ? displayAddress.ToString()
            : "(no LAN address found)";

        _cts = new CancellationTokenSource();
        _ = host.RunAsync(_cts.Token); // fire-and-forget: errors from a single connection are logged inside, not fatal to hosting.
        SetStatus("Waiting for a peer...");

        await ConnectAndStreamAsync(Role.Host, "localhost", pairingCode);
    }

    /// <summary>
    /// Binding every network interface needs a one-time Windows permission grant (a URL ACL) the
    /// first time this account hosts on this port -- <see cref="SignalingServerHost.Start"/>'s doc
    /// comment has the background. Rather than just telling the user to type the netsh command
    /// themselves, request it directly: an elevated one-shot netsh process, which surfaces the
    /// UAC prompt Windows would show anyway. If that's declined (or fails), fall back to
    /// localhost-only so hosting still works for a same-PC test.
    /// </summary>
    private SignalingServerHost? StartSignalingServer()
    {
        var host = new SignalingServerHost("+", SignalingPort, _logger);
        try
        {
            host.Start();
            return host;
        }
        catch (HttpListenerException)
        {
            _logger.Warn("This PC hasn't granted itself permission to accept LAN connections on " +
                         "this port yet -- requesting it now (approve the Windows prompt).");
            if (TryGrantUrlAcl())
            {
                host = new SignalingServerHost("+", SignalingPort, _logger);
                try
                {
                    host.Start();
                    _logger.Info("Permission granted -- hosting on all network interfaces.");
                    return host;
                }
                catch (Exception ex)
                {
                    _logger.Warn($"Still couldn't bind after granting permission: {ex.Message}");
                }
            }
            else
            {
                _logger.Warn("Permission wasn't granted.");
            }
        }

        _logger.Warn("Falling back to this PC only (localhost) -- only this machine can connect until permission is granted.");
        var localOnly = new SignalingServerHost("localhost", SignalingPort, _logger);
        try
        {
            localOnly.Start();
            return localOnly;
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to start the signaling server.", ex);
            return null;
        }
    }

    private bool TryGrantUrlAcl()
    {
        try
        {
            var startInfo = new ProcessStartInfo("netsh",
                $"http add urlacl url=http://+:{SignalingPort}/ user=Everyone")
            {
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var process = Process.Start(startInfo);
            process?.WaitForExit();
            return process?.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            // The user declined the UAC prompt -- not an error, just a "no."
            return false;
        }
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var hostAddress = HostAddressBox.Text.Trim();
        var pairingCode = PairingCodeBox.Text.Trim();
        if (hostAddress.Length == 0 || pairingCode.Length == 0)
        {
            SetStatus("Enter a host address and pairing code.");
            return;
        }

        SetBusy(true);
        _cts = new CancellationTokenSource();
        SetStatus("Connecting...");
        await ConnectAndStreamAsync(Role.Client, hostAddress, pairingCode);
    }

    /// <summary>
    /// Runs on the WPF dispatcher thread throughout -- every await resumes here, which keeps the
    /// UI responsive during the (deliberately unbounded) wait for a peer to join. The pipeline
    /// itself never runs on this thread; only the signaling handshake does. Everything that can
    /// fail on bad user input (a mistyped address, a stray port pasted alongside it, garbage in
    /// the STUN field) lives inside this one try block so it ends up as a status message and a
    /// log line instead of an unhandled exception on the UI thread.
    /// </summary>
    private async Task ConnectAndStreamAsync(Role role, string hostAddressInput, string pairingCode)
    {
        try
        {
            var signalingUri = new Uri($"ws://{SanitizeHostAddress(hostAddressInput)}:{SignalingPort}/");
            var stunServer = ParseEndpoint(StunServerBox.Text, "STUN server");

            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
            {
                ReceiveBufferSize = 4 * 1024 * 1024,
                SendBufferSize = 4 * 1024 * 1024,
            };
            socket.Bind(new IPEndPoint(IPAddress.Any, 0));

            await using var channel = new SignalingClient(signalingUri, _logger);
            var connector = new SignaledPeerConnector(channel, socket, _logger);
            var connection = await connector.ConnectAsync(
                role, pairingCode, stunServer, turn: null, punchTimeout: TimeSpan.FromSeconds(30),
                cancellationToken: _cts!.Token);

            _peerDescription = connection.Describe();
            _logger.Info($"Connected via {_peerDescription}.");

            var options = new SessionOptions
            {
                RemoteInput = RemoteInputCheck.IsChecked == true,
                AdaptiveBitrate = AdaptiveBitrateCheck.IsChecked == true,
                AdaptiveFec = AdaptiveFecCheck.IsChecked == true,
            };

            StartSessionThread(role, connection, options);
            SetStatus($"Streaming ({_peerDescription})");
        }
        catch (OperationCanceledException)
        {
            ReturnToIdle("Cancelled.");
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException or SocketException or WebSocketException)
        {
            // The failure modes bad user input actually produces: an address that won't even
            // parse into a URI, a STUN/host field that isn't host:port, a DNS lookup that came
            // back empty, or -- the most common in practice -- ClientWebSocket wrapping exactly
            // that same DNS failure one level up when the *host* address doesn't resolve. Worth a
            // distinct, non-scary message from the catch-all below, and worth surfacing the inner
            // exception's message since that's where "No such host is known" actually lives.
            var detail = ex.InnerException?.InnerException?.Message ?? ex.InnerException?.Message ?? ex.Message;
            _logger.Warn($"Could not connect: {detail}");
            ReturnToIdle("Check the host address and try again.");
        }
        catch (Exception ex)
        {
            _logger.Error("Connection failed.", ex);
            ReturnToIdle("Connection failed (see log).");
        }
    }

    /// <summary>
    /// Forgiving of the mistakes this field invites: a trailing ":port" (every address already
    /// implies port 7777), stray whitespace, or -- if someone pastes the whole "Others connect
    /// to" line instead of one address -- everything after the first comma.
    /// </summary>
    private static string SanitizeHostAddress(string input)
    {
        var host = input.Split(',')[0].Trim();
        var colonIndex = host.LastIndexOf(':');
        if (colonIndex > 0 && int.TryParse(host[(colonIndex + 1)..], out _))
            host = host[..colonIndex];
        return host;
    }

    /// <summary>
    /// The one address worth reading off to a client on another machine: real LAN/Wi-Fi
    /// adapters have a default gateway, host-only virtual switches (Hyper-V's "Default Switch",
    /// WSL's vEthernet) generally don't -- and those virtual addresses are exactly what showed up
    /// first when this listed every adapter, unreachable from any real second machine.
    /// </summary>
    private static IPAddress? GetPrimaryLanAddress() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic => nic.OperationalStatus == OperationalStatus.Up
                          && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback
                          && nic.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork))
            .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
            .Select(u => u.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
        ?? SignaledPeerConnector.EnumerateLocalIPv4Addresses().FirstOrDefault();

    private void StartSessionThread(Role role, PeerConnection connection, SessionOptions options)
    {
        var token = _cts!.Token;
        var peerDescription = _peerDescription;

        var thread = new Thread(() =>
        {
            try
            {
                if (role == Role.Host)
                {
                    // The host streams to one fixed peer, so it connects -- a no-op on the relay
                    // path, since the transport is already pointed at the relay.
                    connection.Transport.Connect(connection.PeerEndpoint);
                    HostSession.Run(_logger, connection.Transport, peerDescription, options,
                        onStats: stats => Dispatcher.BeginInvoke(() => UpdateStats(stats)), token);
                }
                else
                {
                    ClientSession.Run(_logger, connection.Transport, options,
                        onStats: stats => Dispatcher.BeginInvoke(() => UpdateStats(stats)), token);
                }
            }
            catch (Exception ex)
            {
                _logger.Error("Session ended with an error.", ex);
            }
            finally
            {
                connection.Transport.Dispose();
                // Not Dispatcher.BeginInvoke(ReturnToIdle): ReturnToIdle has an optional parameter,
                // and passing the bare method group to an overloaded Delegate-accepting API doesn't
                // reliably collapse the default value -- confirmed crashing with
                // TargetParameterCountException on a real run. A lambda calling it normally always
                // supplies the default the ordinary way.
                Dispatcher.BeginInvoke(() => ReturnToIdle());
            }
        })
        { IsBackground = true, Name = "rc-session" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        _sessionThread = thread;
    }

    private void UpdateStats(SessionStats stats) =>
        SetStatus($"Streaming ({_peerDescription}) · {stats.Fps:0.#}fps · {stats.RttMs:0}ms rtt");

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        SetStatus("Stopping...");
        StopButton.IsEnabled = false;
    }

    private void ReturnToIdle(string status = "Idle")
    {
        SetBusy(false);
        SetStatus(status);
        PairingCodeDisplay.Text = "";
        LocalAddressesDisplay.Text = "";
        _signalingHost?.Dispose();
        _signalingHost = null;
        _cts?.Dispose();
        _cts = null;
        _sessionThread = null;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        HostModeRadio.IsEnabled = !busy;
        ClientModeRadio.IsEnabled = !busy;
        StartHostButton.IsEnabled = !busy;
        ConnectButton.IsEnabled = !busy;
        HostAddressBox.IsEnabled = !busy;
        PairingCodeBox.IsEnabled = !busy;
        StunServerBox.IsEnabled = !busy;
        StopButton.IsEnabled = busy;
    }

    private void SetStatus(string text) => StatusText.Text = text;

    private static string GeneratePairingCode()
    {
        Span<byte> bytes = stackalloc byte[6];
        RandomNumberGenerator.Fill(bytes);
        var chars = new char[6];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = PairingCodeAlphabet[bytes[i] % PairingCodeAlphabet.Length];
        return new string(chars);
    }

    /// <summary>Same host:port shape LoopbackHarness's --stun-server/--turn-server take.</summary>
    private static IPEndPoint ParseEndpoint(string value, string fieldName)
    {
        var separatorIndex = value.LastIndexOf(':');
        if (separatorIndex < 0 || !int.TryParse(value[(separatorIndex + 1)..], out var port))
            throw new ArgumentException($"{fieldName} requires host:port; got '{value}'.");

        var host = value[..separatorIndex];
        if (IPAddress.TryParse(host, out var literal))
            return new IPEndPoint(literal, port);

        var resolved = Dns.GetHostAddresses(host).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        if (resolved is null)
            throw new ArgumentException($"{fieldName} host '{host}' did not resolve to an IPv4 address.");
        return new IPEndPoint(resolved, port);
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        _cts?.Cancel();
        _sessionThread?.Join(TimeSpan.FromSeconds(5));
        _signalingHost?.Dispose();
    }
}
