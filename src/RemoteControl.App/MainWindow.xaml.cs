using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
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

        var host = new SignalingServerHost("+", SignalingPort, _logger);
        try
        {
            host.Start();
        }
        catch (HttpListenerException)
        {
            // Binding every interface needs admin or a one-time URL ACL -- see
            // SignalingServerHost.Start's remarks. Falling back to loopback keeps hosting usable
            // for a same-PC test but only this machine can reach it until that's granted.
            _logger.Warn("Could not bind all network interfaces -- this needs administrator rights " +
                         "or a one-time permission. Run this once, as administrator, then try again:  " +
                         $"netsh http add urlacl url=http://+:{SignalingPort}/ user=Everyone");
            _logger.Warn("Falling back to this PC only (localhost) for now.");
            host = new SignalingServerHost("localhost", SignalingPort, _logger);
            try
            {
                host.Start();
            }
            catch (Exception ex)
            {
                _logger.Error("Failed to start the signaling server.", ex);
                SetStatus("Failed to start (see log).");
                SetBusy(false);
                return;
            }
        }

        _signalingHost = host;
        var addresses = SignaledPeerConnector.EnumerateLocalIPv4Addresses();
        LocalAddressesDisplay.Text = addresses.Count > 0
            ? string.Join(", ", addresses.Select(a => $"{a}:{SignalingPort}"))
            : "(no LAN address found)";

        _cts = new CancellationTokenSource();
        _ = host.RunAsync(_cts.Token); // fire-and-forget: errors from a single connection are logged inside, not fatal to hosting.
        SetStatus("Waiting for a peer...");

        await ConnectAndStreamAsync(Role.Host, new Uri($"ws://localhost:{SignalingPort}/"), pairingCode);
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
        await ConnectAndStreamAsync(Role.Client, new Uri($"ws://{hostAddress}:{SignalingPort}/"), pairingCode);
    }

    /// <summary>
    /// Runs on the WPF dispatcher thread throughout -- every await resumes here, which keeps the
    /// UI responsive during the (deliberately unbounded) wait for a peer to join. The pipeline
    /// itself never runs on this thread; only the signaling handshake does.
    /// </summary>
    private async Task ConnectAndStreamAsync(Role role, Uri signalingUri, string pairingCode)
    {
        try
        {
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
            SetStatus("Cancelled.");
            ReturnToIdle();
        }
        catch (Exception ex)
        {
            _logger.Error("Connection failed.", ex);
            SetStatus("Connection failed (see log).");
            ReturnToIdle();
        }
    }

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
                Dispatcher.BeginInvoke(ReturnToIdle);
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

    private void ReturnToIdle()
    {
        SetBusy(false);
        SetStatus("Idle");
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
