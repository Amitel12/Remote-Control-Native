using RemoteControl.Common;
using Vortice;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace RemoteControl.Render;

public enum PresentOutcome
{
    Presented,
    Occluded,
    SkippedWhileMinimized,
}

/// <summary>
/// Presents decoded NV12 D3D11 textures through the D3D11 video processor.
/// The input view selects the decoder's real array slice; this must never be
/// replaced with CopyResource because the Microsoft decoder uses texture arrays.
/// </summary>
public sealed class SwapChainPresenter : IDisposable
{
    private const int DxgiStatusOccluded = 0x087A0001;

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11VideoDevice _videoDevice;
    private readonly ID3D11VideoContext _videoContext;
    private readonly uint _sourceWidth;
    private readonly uint _sourceHeight;
    private readonly ILogger _logger;
    private readonly IDXGISwapChain1 _swapChain;

    private ID3D11VideoProcessorEnumerator? _enumerator;
    private ID3D11VideoProcessor? _processor;
    private ID3D11Texture2D? _backBuffer;
    private ID3D11VideoProcessorOutputView? _outputView;
    private uint _outputWidth;
    private uint _outputHeight;
    private uint _outputFrame;
    private bool _disposed;

    public uint OutputWidth => _outputWidth;
    public uint OutputHeight => _outputHeight;

    public SwapChainPresenter(
        ID3D11Device device,
        ID3D11DeviceContext context,
        nint windowHandle,
        uint sourceWidth,
        uint sourceHeight,
        uint outputWidth,
        uint outputHeight,
        ILogger? logger = null)
    {
        if (windowHandle == 0)
            throw new ArgumentException("A valid HWND is required.", nameof(windowHandle));
        if (sourceWidth == 0 || sourceHeight == 0)
            throw new ArgumentOutOfRangeException(nameof(sourceWidth), "Source dimensions must be non-zero.");

        _device = device ?? throw new ArgumentNullException(nameof(device));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _device.AddRef();
        _context.AddRef();
        _sourceWidth = sourceWidth;
        _sourceHeight = sourceHeight;
        _logger = logger ?? new ConsoleLogger(nameof(SwapChainPresenter));
        _videoDevice = device.QueryInterface<ID3D11VideoDevice>();
        _videoContext = context.QueryInterface<ID3D11VideoContext>();

        _outputWidth = Math.Max(outputWidth, 1);
        _outputHeight = Math.Max(outputHeight, 1);
        var description = new SwapChainDescription1(
            _outputWidth,
            _outputHeight,
            Format.B8G8R8A8_UNorm,
            false,
            Usage.RenderTargetOutput,
            2,
            Scaling.Stretch,
            SwapEffect.FlipDiscard,
            AlphaMode.Ignore,
            SwapChainFlags.None);

        using var factory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false);
        _swapChain = factory.CreateSwapChainForHwnd(device, windowHandle, description);
        factory.MakeWindowAssociation(windowHandle, WindowAssociationFlags.IgnoreAltEnter).CheckError();

        CreateSizeDependentResources();
        _logger.Info($"Swap chain presenter ready: NV12 {_sourceWidth}x{_sourceHeight} -> BGRA {_outputWidth}x{_outputHeight}.");
    }

    public void Resize(uint width, uint height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (width == 0 || height == 0)
        {
            _outputWidth = 0;
            _outputHeight = 0;
            return;
        }

        if (width == _outputWidth && height == _outputHeight)
            return;

        ReleaseSizeDependentResources();
        _swapChain.ResizeBuffers(2, width, height, Format.B8G8R8A8_UNorm, SwapChainFlags.None).CheckError();
        _outputWidth = width;
        _outputHeight = height;
        CreateSizeDependentResources();
        _logger.Info($"Swap chain resized to {width}x{height}.");
    }

    public PresentOutcome Present(ID3D11Texture2D texture, uint subresourceIndex = 0, uint syncInterval = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(texture);
        if (_outputWidth == 0 || _outputHeight == 0 || _outputView is null || _processor is null || _enumerator is null)
            return PresentOutcome.SkippedWhileMinimized;

        var description = new VideoProcessorInputViewDescription
        {
            ViewDimension = VideoProcessorInputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorInputView
            {
                MipSlice = 0,
                ArraySlice = subresourceIndex,
            },
        };

        _videoDevice.CreateVideoProcessorInputView(texture, _enumerator, description, out var inputView).CheckError();
        using (inputView)
        {
            var streams = new[]
            {
                new VideoProcessorStream
                {
                    Enable = true,
                    OutputIndex = 0,
                    InputFrameOrField = 0,
                    PastFrames = 0,
                    FutureFrames = 0,
                    InputSurface = inputView,
                },
            };

            _videoContext.VideoProcessorBlt(_processor, _outputView, _outputFrame++, 1, streams).CheckError();
        }

        var result = _swapChain.Present(syncInterval, PresentFlags.None);
        if (result.Code == DxgiStatusOccluded)
            return PresentOutcome.Occluded;

        result.CheckError();
        return PresentOutcome.Presented;
    }

    private void CreateSizeDependentResources()
    {
        var content = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputFrameRate = new Rational(60, 1),
            InputWidth = _sourceWidth,
            InputHeight = _sourceHeight,
            OutputFrameRate = new Rational(60, 1),
            OutputWidth = _outputWidth,
            OutputHeight = _outputHeight,
            Usage = VideoUsage.PlaybackNormal,
        };

        _videoDevice.CreateVideoProcessorEnumerator(ref content, out _enumerator).CheckError();
        _videoDevice.CreateVideoProcessor(_enumerator, 0, out _processor).CheckError();

        _backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        var outputDescription = new VideoProcessorOutputViewDescription
        {
            ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 },
        };
        _videoDevice.CreateVideoProcessorOutputView(_backBuffer, _enumerator, outputDescription, out _outputView).CheckError();

        _videoContext.VideoProcessorSetStreamFrameFormat(_processor, 0, VideoFrameFormat.Progressive);
        _videoContext.VideoProcessorSetStreamSourceRect(
            _processor, 0, true, new RawRect(0, 0, (int)_sourceWidth, (int)_sourceHeight));
        _videoContext.VideoProcessorSetStreamDestRect(
            _processor, 0, true, new RawRect(0, 0, (int)_outputWidth, (int)_outputHeight));
        _videoContext.VideoProcessorSetOutputTargetRect(
            _processor, true, new RawRect(0, 0, (int)_outputWidth, (int)_outputHeight));
    }

    private void ReleaseSizeDependentResources()
    {
        _outputView?.Dispose();
        _outputView = null;
        _backBuffer?.Dispose();
        _backBuffer = null;
        _processor?.Dispose();
        _processor = null;
        _enumerator?.Dispose();
        _enumerator = null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        ReleaseSizeDependentResources();
        _swapChain.Dispose();
        _videoContext.Dispose();
        _videoDevice.Dispose();
        _context.Release();
        _device.Release();
    }
}
