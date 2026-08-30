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

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ILogger _logger;
    private readonly uint _outputIndex;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _captureTexture;
    private bool _frameOutstanding;
    private bool _disposed;

    public DisplayInfo Display { get; }
    public uint Width => (uint)Display.Width;
    public uint Height => (uint)Display.Height;
    public Format Format => _duplication?.Description.ModeDescription.Format ?? Format.Unknown;

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

        CreateDuplication();
        _logger.Info($"Desktop Duplication ready: {Display.DeviceName}, {Width}x{Height}, {Format}, rotation {Display.Rotation}.");
    }

    public bool TryAcquireNextFrame(uint timeoutMilliseconds, out DesktopFrame? frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_frameOutstanding)
            throw new InvalidOperationException("Dispose the previous DesktopFrame before acquiring another one.");

        frame = null;
        var result = _duplication!.AcquireNextFrame(timeoutMilliseconds, out var information, out var resource);
        if (result.Code == DxgiErrorWaitTimeout)
            return false;

        if (result.Code == DxgiErrorAccessLost)
        {
            _logger.Warn("Desktop Duplication access was lost; recreating the duplication session.");
            RecreateDuplication();
            return false;
        }

        result.CheckError();
        try
        {
            using (resource)
            using (var acquiredTexture = resource.QueryInterface<ID3D11Texture2D>())
            {
                // Desktop Duplication surfaces have no bind flags. The Video
                // Processor MFT rejects them with DXGI_ERROR_INVALID_CALL, so
                // normalize into one reusable RenderTarget texture. This is a
                // GPU CopyResource only: no pixel data reaches the CPU.
                _context.CopyResource(_captureTexture!, acquiredTexture);
            }
        }
        finally
        {
            _duplication.ReleaseFrame().CheckError();
        }

        _frameOutstanding = true;
        frame = new DesktopFrame(
            _captureTexture!.QueryInterface<ID3D11Texture2D>(),
            information,
            () => _frameOutstanding = false);
        return true;
    }

    private void CreateDuplication()
    {
        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        dxgiDevice.GetAdapter(out var adapter).CheckError();
        using (adapter)
        {
            adapter.EnumOutputs(_outputIndex, out var output).CheckError();
            using (output)
            using (var output1 = output.QueryInterface<IDXGIOutput1>())
            {
                _duplication = output1.DuplicateOutput(_device);
            }
        }

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

    private void RecreateDuplication()
    {
        _duplication?.Dispose();
        _duplication = null;
        CreateDuplication();
    }

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
