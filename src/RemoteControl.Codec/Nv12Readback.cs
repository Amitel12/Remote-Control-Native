using Vortice.Direct3D11;
using Vortice.DXGI;

namespace RemoteControl.Codec;

/// <summary>
/// Reads an NV12 D3D11 texture back to a tightly-packed CPU byte array
/// (Y plane, then interleaved UV, no row padding). Only used by
/// <see cref="HardwareEncoder"/>'s system-memory fallback, itself only
/// reached when the hardware encoder MFT rejects D3D11 samples outright --
/// see docs/PHASE-0.md. Not part of the steady-state zero-copy path.
/// </summary>
internal static class Nv12Readback
{
    public static byte[] ToPackedBytes(
        ID3D11Device device, ID3D11DeviceContext context, ID3D11Texture2D texture, uint width, uint height,
        uint subresourceIndex = 0)
    {
        using var staging = device.CreateTexture2D(new Texture2DDescription(
            Format.NV12, width, height, arraySize: 1, mipLevels: 1,
            BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read, sampleCount: 1, sampleQuality: 0, ResourceOptionFlags.None));

        // CopySubresourceRegion targeting the specific slice, not CopyResource:
        // some D3D11-aware MFTs (confirmed for the H.264 decoder, see
        // HardwareDecoder/DecodedFrame) hand back one slice of a texture
        // *array* they manage as an internal pool. CopyResource against a
        // mismatched array size is a silent no-op, not an error -- it looks
        // like a successful copy of all-zero data. See docs/PHASE-0.md.
        context.CopySubresourceRegion(staging, 0, 0, 0, 0, texture, subresourceIndex, null);

        var mapped = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var packed = new byte[width * height + width * height / 2];
            unsafe
            {
                var yPlane = (byte*)mapped.DataPointer;
                var uvPlane = yPlane + mapped.RowPitch * height;

                fixed (byte* dst = packed)
                {
                    for (var y = 0; y < height; y++)
                        Buffer.MemoryCopy(yPlane + y * mapped.RowPitch, dst + y * width, width, width);

                    var uvDst = dst + width * height;
                    for (var y = 0; y < height / 2; y++)
                        Buffer.MemoryCopy(uvPlane + y * mapped.RowPitch, uvDst + y * width, width, width);
                }
            }
            return packed;
        }
        finally
        {
            context.Unmap(staging, 0);
        }
    }
}
