using Vortice.Direct3D11;
using Vortice.DXGI;

namespace RemoteControl.Capture;

public sealed record DisplayInfo(
    uint OutputIndex,
    string DeviceName,
    int Left,
    int Top,
    int Width,
    int Height,
    ModeRotation Rotation);

/// <summary>Enumerates outputs attached to the adapter that owns a D3D11 device.</summary>
public static class DisplayEnumerator
{
    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);

    public static IReadOnlyList<DisplayInfo> Enumerate(ID3D11Device device)
    {
        ArgumentNullException.ThrowIfNull(device);

        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        dxgiDevice.GetAdapter(out var adapter).CheckError();
        using (adapter)
        {
            var displays = new List<DisplayInfo>();
            for (uint index = 0; ; index++)
            {
                var result = adapter.EnumOutputs(index, out var output);
                if (result.Code == DxgiErrorNotFound)
                    break;
                result.CheckError();

                using (output)
                {
                    var description = output.Description;
                    if (!description.AttachedToDesktop)
                        continue;

                    var bounds = description.DesktopCoordinates;
                    displays.Add(new DisplayInfo(
                        index,
                        description.DeviceName,
                        bounds.Left,
                        bounds.Top,
                        bounds.Right - bounds.Left,
                        bounds.Bottom - bounds.Top,
                        description.Rotation));
                }
            }

            return displays;
        }
    }
}
