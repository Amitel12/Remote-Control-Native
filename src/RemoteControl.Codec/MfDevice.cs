using RemoteControl.Common;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;

namespace RemoteControl.Codec;

/// <summary>
/// The D3D11 device + IMFDXGIDeviceManager pair every MFT in this pipeline
/// shares (see docs/PHASE-0.md, "D3D11 device setup for MF"): one device
/// with VideoSupport, marked multithread-protected because MF touches it
/// from its own threads, wrapped in a device manager so
/// MFT_MESSAGE_SET_D3D_MANAGER can hand it to each transform.
/// </summary>
public sealed class MfDevice : IDisposable
{
    public ID3D11Device Device { get; }
    public ID3D11DeviceContext ImmediateContext { get; }
    public IMFDXGIDeviceManager DeviceManager { get; }

    private MfDevice(ID3D11Device device, ID3D11DeviceContext context, IMFDXGIDeviceManager deviceManager)
    {
        Device = device;
        ImmediateContext = context;
        DeviceManager = deviceManager;
    }

    public static MfDevice Create(ILogger? logger = null)
    {
        var log = logger ?? new ConsoleLogger(nameof(MfDevice));

        var flags = DeviceCreationFlags.VideoSupport | DeviceCreationFlags.BgraSupport;
        var featureLevels = new[]
        {
            FeatureLevel.Level_11_1,
            FeatureLevel.Level_11_0,
        };

        // Explicit adapter, not IntPtr.Zero/default: on a hybrid-graphics
        // machine (integrated + discrete GPU) the default adapter is not
        // guaranteed to be the one the hardware encoder MFT is bound to, and
        // a mismatch there surfaces as MF_E_UNSUPPORTED_D3D_TYPE from
        // ProcessInput with no indication it's an adapter problem -- see
        // docs/PHASE-0.md. Adapter 0 from EnumAdapters1 is the primary
        // adapter, which is the discrete GPU on every machine tested so far.
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        factory.EnumAdapters1(0, out var adapter).CheckError();

        ID3D11Device device;
        ID3D11DeviceContext context;
        using (adapter)
        {
            log.Info($"D3D11 adapter: {adapter.Description1.Description} (vendor 0x{adapter.Description1.VendorId:X4}).");

            var result = D3D11.D3D11CreateDevice(
                adapter, DriverType.Unknown, flags, featureLevels,
                out device, out context);
            result.CheckError();
        }

        // MF drives the device from its own worker threads (the async MFT
        // event loop, internal DXVA threads) concurrently with this thread --
        // without this, that shows up as sporadic corruption/crashes far from
        // the actual cause.
        using (var multithread = device.QueryInterface<ID3D11Multithread>())
        {
            multithread.SetMultithreadProtected(true);
        }

        var deviceManager = MediaFactory.MFCreateDXGIDeviceManager();
        deviceManager.ResetDevice(device).CheckError();

        log.Info($"D3D11 device created (feature level {device.FeatureLevel}), IMFDXGIDeviceManager bound.");

        return new MfDevice(device, context, deviceManager);
    }

    public void Dispose()
    {
        DeviceManager.Dispose();
        ImmediateContext.Dispose();
        Device.Dispose();
    }
}
