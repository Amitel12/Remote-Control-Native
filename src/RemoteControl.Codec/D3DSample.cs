using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace RemoteControl.Codec;

/// <summary>
/// Wraps an existing D3D11 texture as an IMFSample via MFCreateDXGISurfaceBuffer
/// instead of a memory buffer -- this is the entire zero-copy mechanism: the
/// sample references the GPU texture directly, no CPU-side pixel data ever
/// changes hands.
/// </summary>
public static class D3DSample
{
    public static IMFSample Wrap(ID3D11Texture2D texture, long sampleTime, long sampleDuration, uint subresourceIndex = 0)
    {
        using var buffer = MediaFactory.MFCreateDXGISurfaceBuffer(
            typeof(ID3D11Texture2D).GUID, texture, subresourceIndex, bottomUpWhenLinear: false);

        var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = sampleTime;
        sample.SampleDuration = sampleDuration;
        return sample;
    }
}
