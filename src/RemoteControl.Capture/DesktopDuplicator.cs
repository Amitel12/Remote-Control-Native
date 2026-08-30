using RemoteControl.Common;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace RemoteControl.Capture;

/// <summary>
/// Captures one desktop output with DXGI Desktop Duplication. The acquired
/// surface is normalized with a GPU CopyResource into a reusable render-target
/// texture; callers must dispose the returned frame before acquiring another.
/// </summary>
public sealed class DesktopDuplicator : IDisposable
{
    private const int DxgiErrorAccessLost = unchecked((int)0x887A0026);
    private const int DxgiErrorWaitTimeout = unchecked((int)0x887A0027);
    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);
    private const int DxgiErrorNotCurrentlyAvailable = unchecked((int)0x887A0022);
    private const int DxgiErrorSessionDisconnected = unchecked((int)0x887A0028);
    private const int DxgiErrorUnsupported = unchecked((int)0x887A0004);
    private const int EAccessDenied = unchecked((int)0x80070005);
    private const int RecoveryRetryMilliseconds = 500;

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ILogger _logger;
    private readonly uint _outputIndex;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _captureTexture;
    private uint _width;
    private uint _height;
    private Format _format;
    private long _nextRecoveryAttempt;
    private bool _waitingForDesktop;
    private bool _frameOutstanding;
    private bool _disposed;

    public DisplayInfo Display { get; }
    public uint Width => _width;
    public uint Height => _height;
    public Format Format => _format;

    public DesktopDuplicator(
        ID3D11Device device,
        ID3D11DeviceContext context,
        uint outputIndex = 0,
        ILogger? logger = null)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _device.AddRef();
        _context.AddRef();
        _outputIndex = outputIndex;
        _logger = logger ?? new ConsoleLogger(nameof(DesktopDuplicator));

        Display = DisplayEnumerator.Enumerate(device).FirstOrDefault(x => x.OutputIndex == outputIndex)
            ?? throw new ArgumentOutOfRangeException(nameof(outputIndex), $"No attached output {outputIndex} exists on this D3D11 adapter.");

        try
        {
            CreateDuplication(checkForModeChange: false);
            _logger.Info($"Desktop Duplication ready: {Display.DeviceName}, {Width}x{Height}, {Format}, rotation {Display.Rotation}.");
        }
        catch (Exception ex) when (IsTemporaryDuplicationFailure(ex.HResult))
        {
            _width = (uint)Display.Width;
            _height = (uint)Display.Height;
            _format = Format.B8G8R8A8_UNorm;
            _waitingForDesktop = true;
            _logger.Warn("Desktop capture is temporarily unavailable; waiting for the interactive desktop.");
        }
    }

    public bool TryAcquireNextFrame(uint timeoutMilliseconds, out DesktopFrame? frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_frameOutstanding)
            throw new InvalidOperationException("Dispose the previous DesktopFrame before acquiring another one.");

        frame = null;
        if (_duplication is null)
        {
            TryRestoreDuplication();
            return false;
        }

        var result = _duplication!.AcquireNextFrame(timeoutMilliseconds, out var information, out var resource);
        if (result.Code == DxgiErrorWaitTimeout)
            return false;

        if (result.Code == DxgiErrorAccessLost)
        {
            BeginRecovery();
            TryRestoreDuplication();
            return false;
        }

        result.CheckError();
        var releaseLostAccess = false;
        try
        {
            using (resource)
            using (var acquiredTexture = resource.QueryInterface<ID3D11Texture2D>())
            {
                var acquiredDescription = acquiredTexture.Description;
                if (acquiredDescription.Width != Width || acquiredDescription.Height != Height)
                {
                    throw new DesktopConfigurationChangedException(
                        Width,
                        Height,
                        acquiredDescription.Width,
                        acquiredDescription.Height);
                }

                // Desktop Duplication surfaces have no bind flags. The Video
                // Processor MFT rejects them with DXGI_ERROR_INVALID_CALL, so
                // normalize into one reusable RenderTarget texture. This is a
                // GPU CopyResource only: no pixel data reaches the CPU.
                _context.CopyResource(_captureTexture!, acquiredTexture);
            }
        }
        finally
        {
            var releaseResult = _duplication.ReleaseFrame();
            if (releaseResult.Code == DxgiErrorAccessLost)
            {
                // A desktop switch can happen after AcquireNextFrame succeeds
                // but before ReleaseFrame. DXGI reports the transition here
                // instead of on the next acquire, so enter the same recovery
                // path and discard this now-invalid frame.
                releaseLostAccess = true;
                BeginRecovery();
            }
            else
            {
                releaseResult.CheckError();
            }
        }

        if (releaseLostAccess)
            return false;

        _frameOutstanding = true;
        frame = new DesktopFrame(
            _captureTexture!.QueryInterface<ID3D11Texture2D>(),
            information,
            () => _frameOutstanding = false);
        return true;
    }

    private void CreateDuplication(bool checkForModeChange)
    {
        IDXGIOutputDuplication? newDuplication = null;
        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        dxgiDevice.GetAdapter(out var adapter).CheckError();
        using (adapter)
        {
            adapter.EnumOutputs(_outputIndex, out var output).CheckError();
            using (output)
            using (var output1 = output.QueryInterface<IDXGIOutput1>())
            {
                newDuplication = output1.DuplicateOutput(_device);
            }
        }

        var mode = newDuplication.Description.ModeDescription;
        if (checkForModeChange &&
            (_width != mode.Width || _height != mode.Height || _format != mode.Format))
        {
            newDuplication.Dispose();
            throw new DesktopConfigurationChangedException(_width, _height, mode.Width, mode.Height);
        }

        _width = mode.Width;
        _height = mode.Height;
        _format = mode.Format;
        _duplication = newDuplication;

        _captureTexture?.Dispose();
        _captureTexture = _device.CreateTexture2D(new Texture2DDescription(
            Format.B8G8R8A8_UNorm,
            Width,
            Height,
            arraySize: 1,
            mipLevels: 1,
            BindFlags.RenderTarget,
            ResourceUsage.Default,
            CpuAccessFlags.None,
            sampleCount: 1,
            sampleQuality: 0,
            ResourceOptionFlags.None));
    }

    private void BeginRecovery()
    {
        if (!_waitingForDesktop)
            _logger.Warn("Desktop Duplication access was lost; waiting for the interactive desktop to become available.");

        _waitingForDesktop = true;
        _duplication?.Dispose();
        _duplication = null;
        _captureTexture?.Dispose();
        _captureTexture = null;
        _nextRecoveryAttempt = 0;
    }

    private bool TryRestoreDuplication()
    {
        var now = Environment.TickCount64;
        if (now < _nextRecoveryAttempt)
            return false;

        _nextRecoveryAttempt = now + RecoveryRetryMilliseconds;
        try
        {
            CreateDuplication(checkForModeChange: true);
            _waitingForDesktop = false;
            _logger.Info($"Desktop Duplication restored at {Width}x{Height}, {Format}.");
            return true;
        }
        catch (Exception ex) when (IsTemporaryDuplicationFailure(ex.HResult))
        {
            // Lock/UAC switches Windows to a secure desktop. Microsoft
            // explicitly documents E_ACCESSDENIED here for non-SYSTEM
            // processes; retry after the user returns to the interactive
            // desktop instead of terminating the stream.
            return false;
        }
    }

    private static bool IsTemporaryDuplicationFailure(int hresult) =>
        hresult is EAccessDenied or
            DxgiErrorNotFound or
            DxgiErrorNotCurrentlyAvailable or
            DxgiErrorSessionDisconnected or
            DxgiErrorUnsupported;

    private void ReleaseFrame()
    {
        _frameOutstanding = false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        ReleaseFrame();
        _captureTexture?.Dispose();
        _duplication?.Dispose();
        _context.Release();
        _device.Release();
    }
}

public sealed class DesktopConfigurationChangedException : Exception
{
    public uint PreviousWidth { get; }
    public uint PreviousHeight { get; }
    public uint NewWidth { get; }
    public uint NewHeight { get; }

    public DesktopConfigurationChangedException(
        uint previousWidth,
        uint previousHeight,
        uint newWidth,
        uint newHeight)
        : base($"Desktop capture changed from {previousWidth}x{previousHeight} to {newWidth}x{newHeight}.")
    {
        PreviousWidth = previousWidth;
        PreviousHeight = previousHeight;
        NewWidth = newWidth;
        NewHeight = newHeight;
    }
}

public sealed class DesktopFrame : IDisposable
{
    private Action? _release;

    public ID3D11Texture2D Texture { get; }
    public OutduplFrameInfo Information { get; }

    internal DesktopFrame(ID3D11Texture2D texture, OutduplFrameInfo information, Action release)
    {
        Texture = texture;
        Information = information;
        _release = release;
    }

    public void Dispose()
    {
        Texture.Dispose();
        Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
