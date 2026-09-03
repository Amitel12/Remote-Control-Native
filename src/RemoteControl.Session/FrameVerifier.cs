using System.Drawing;
using System.Drawing.Imaging;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace RemoteControl.Session;

/// <summary>
/// One-off correctness check (docs/PHASE-0.md's first exit-criterion
/// question, "is the output correct?"): reads a decoded NV12 D3D11 texture
/// back to CPU and writes it out as a PNG. Deliberately isolated to this one
/// file/call site -- a CPU readback here is fine as a one-time check, but
/// would corrupt the *second* exit-criterion question ("are there CPU
/// copies?") if it were part of the steady-state pipeline instead of a
/// removable side step.
/// </summary>
public static class FrameVerifier
{
    public static void SaveNv12FrameAsPng(
        ID3D11Device device, ID3D11DeviceContext context, ID3D11Texture2D nv12Texture, string outputPath,
        uint subresourceIndex = 0)
    {
        var desc = nv12Texture.Description;
        var width = (int)desc.Width;
        var height = (int)desc.Height;

        using var staging = device.CreateTexture2D(new Texture2DDescription(
            Format.NV12, desc.Width, desc.Height, arraySize: 1, mipLevels: 1,
            BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read, sampleCount: 1, sampleQuality: 0, ResourceOptionFlags.None));

        // CopySubresourceRegion targeting the specific slice, not CopyResource
        // -- see RemoteControl.Codec.DecodedFrame. A mismatched-array-size
        // CopyResource is a silent no-op, not an error.
        context.CopySubresourceRegion(staging, 0, 0, 0, 0, nv12Texture, subresourceIndex, null);

        var mapped = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            var bmpData = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            try
            {
                unsafe
                {
                    var yPlane = (byte*)mapped.DataPointer;
                    var uvPlane = (byte*)mapped.DataPointer + mapped.RowPitch * height;
                    var dst = (byte*)bmpData.Scan0;

                    for (var y = 0; y < height; y++)
                    {
                        var yRow = yPlane + y * mapped.RowPitch;
                        var uvRow = uvPlane + (y / 2) * mapped.RowPitch;
                        var dstRow = dst + y * bmpData.Stride;

                        for (var x = 0; x < width; x++)
                        {
                            var yValue = yRow[x];
                            var u = uvRow[(x / 2) * 2] - 128;
                            var v = uvRow[(x / 2) * 2 + 1] - 128;

                            var r = Clamp(yValue + 1.402 * v);
                            var g = Clamp(yValue - 0.344136 * u - 0.714136 * v);
                            var b = Clamp(yValue + 1.772 * u);

                            var p = dstRow + x * 3;
                            p[0] = b; p[1] = g; p[2] = r; // Bitmap Format24bppRgb is BGR-order in memory.
                        }
                    }
                }
            }
            finally
            {
                bitmap.UnlockBits(bmpData);
            }

            bitmap.Save(outputPath, ImageFormat.Png);
        }
        finally
        {
            context.Unmap(staging, 0);
        }
    }

    private static byte Clamp(double v) => (byte)Math.Clamp(v, 0, 255);
}
