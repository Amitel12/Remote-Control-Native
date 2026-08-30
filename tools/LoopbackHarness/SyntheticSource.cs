using Vortice;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace RemoteControl.Tools.LoopbackHarness;

/// <summary>
/// Phase 0 Step 1's synthetic source (see docs/PHASE-0.md): a 1920x1080
/// BGRA D3D11 texture with a moving filled rectangle, so successive frames
/// actually differ -- a static texture would let the encoder degenerate to
/// "skip everything," which proves nothing about the pipeline. Draws with
/// two GPU-side ID3D11DeviceContext1.ClearView calls (full-frame background,
/// then a sub-rectangle) rather than a shader -- no HLSL needed for a solid
/// moving box, and it's still entirely GPU-resident, matching Step 1's
/// zero-copy requirement between here and the encoder.
/// </summary>
public sealed class SyntheticSource : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext1 _context1;
    private readonly uint _width;
    private readonly uint _height;
    private int _frameIndex;

    public SyntheticSource(ID3D11Device device, ID3D11DeviceContext context, uint width, uint height)
    {
        _device = device;
        _context1 = context.QueryInterface<ID3D11DeviceContext1>();
        _width = width;
        _height = height;
    }

    /// <summary>Renders and returns the next frame. Caller owns and disposes the returned texture.</summary>
    public ID3D11Texture2D NextFrame()
    {
        var texture = _device.CreateTexture2D(new Texture2DDescription(
            Format.B8G8R8A8_UNorm, _width, _height, arraySize: 1, mipLevels: 1,
            BindFlags.RenderTarget, ResourceUsage.Default, CpuAccessFlags.None,
            sampleCount: 1, sampleQuality: 0, ResourceOptionFlags.None));

        using var rtv = _device.CreateRenderTargetView(texture, null);

        // Neither the 2-arg ClearView(view, color) convenience overload (recurses
        // into itself -- stack overflow) nor the 3-arg form with null rects
        // (NullReferenceException marshaling the array) works in this Vortice
        // version. An explicit whole-texture rect sidesteps both bugs.
        var wholeTexture = new RawRect(0, 0, (int)_width, (int)_height);
        _context1.ClearView(rtv, new Color4(0.05f, 0.05f, 0.1f, 1f), new[] { wholeTexture });

        const int rectSize = 160;
        var travel = (int)_width - rectSize;
        var m = _frameIndex % (2 * travel);
        var x = m < travel ? m : 2 * travel - m; // bounce back and forth.
        var y = ((int)_height - rectSize) / 2;
        var rect = new RawRect(x, y, x + rectSize, y + rectSize);
        _context1.ClearView(rtv, new Color4(0.9f, 0.35f, 0.1f, 1f), new[] { rect });

        _frameIndex++;
        return texture;
    }

    public void Dispose() => _context1.Dispose();
}
