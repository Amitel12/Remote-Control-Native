using System.Runtime.InteropServices;

namespace RemoteControl.Codec.Interop;

/// <summary>
/// Vortice.MediaFoundation does not bind ICodecAPI (it lives in Codecapi.h /
/// Icodecapi.h, outside Media Foundation proper), but the H.264 encoder's
/// B-frame count is only reachable through it -- MF_LOW_LATENCY is an
/// IMFAttributes value and doesn't need this, but
/// CODECAPI_AVEncMPVDefaultBPictureCount does. Hand-rolled classic COM
/// interop via [ComImport] works fine on net8.0-windows.
///
/// Only the prefix of the real vtable this code actually calls is declared
/// (IsSupported/IsModifiable/GetParameterRange/GetParameterValues/
/// GetDefaultValue/GetValue/SetValue) -- COM vtable slots are positional, so
/// a contiguous prefix declared in the real interface's order is valid even
/// though RegisterForEvent and friends are omitted.
/// </summary>
[ComImport]
[Guid("901DB4C7-31CE-41A2-85DC-8FA0BF41B8DA")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICodecApi
{
    [PreserveSig] int IsSupported([In] ref Guid api);
    [PreserveSig] int IsModifiable([In] ref Guid api);
    [PreserveSig] int GetParameterRange([In] ref Guid api, out object valueMin, out object valueMax, out object steppingDelta);
    [PreserveSig] int GetParameterValues([In] ref Guid api, out IntPtr values, out uint valuesCount);
    [PreserveSig] int GetDefaultValue([In] ref Guid api, out object value);
    [PreserveSig] int GetValue([In] ref Guid api, out object value);
    [PreserveSig] int SetValue([In] ref Guid api, [In] ref object value);
}

/// <summary>
/// GUIDs Vortice doesn't expose. Values are the documented Win32 constants
/// from Codecapi.h / Mfapi.h -- see docs/PHASE-0.md for the sources checked.
/// </summary>
internal static class CodecApiGuids
{
    public static readonly Guid AVEncMPVDefaultBPictureCount = new("8D390AAC-DC5C-4200-B57F-814D04BABAB2");

    /// <summary>
    /// MF_LOW_LATENCY (Mfapi.h). Documented as numerically identical to
    /// CODECAPI_AVLowLatencyMode -- set directly via IMFAttributes.Set,
    /// no ICodecAPI needed for this one.
    /// </summary>
    public static readonly Guid MfLowLatency = new("9C27891A-ED7A-40E1-88E8-B22727A024EE");
}

/// <summary>
/// QueryInterface's an IMFTransform for ICodecAPI and sets one property,
/// tolerating encoders that don't support it -- not every H.264 encoder MFT
/// implements every CODECAPI property, and that's a configuration fallback,
/// not a failure.
/// </summary>
internal static class CodecApiHelper
{
    public static bool TrySetValue(IntPtr transformNativePointer, Guid property, object value, out string? failureReason)
    {
        failureReason = null;
        var iid = typeof(ICodecApi).GUID;
        var hr = Marshal.QueryInterface(transformNativePointer, ref iid, out var codecApiPtr);
        if (hr < 0)
        {
            failureReason = $"transform does not expose ICodecAPI (0x{hr:X8})";
            return false;
        }

        try
        {
            var codecApi = (ICodecApi)Marshal.GetObjectForIUnknown(codecApiPtr);
            try
            {
                // IsSupported returns S_OK (0) if supported, S_FALSE (1) if not -- not
                // a failure HRESULT either way, so check the value, not the sign.
                var supportHr = codecApi.IsSupported(ref property);
                if (supportHr != 0)
                {
                    failureReason = $"property not supported (IsSupported returned 0x{supportHr:X8})";
                    return false;
                }

                var setHr = codecApi.SetValue(ref property, ref value);
                if (setHr < 0)
                {
                    failureReason = $"SetValue failed (0x{setHr:X8})";
                    return false;
                }

                return true;
            }
            finally
            {
                Marshal.ReleaseComObject(codecApi);
            }
        }
        finally
        {
            Marshal.Release(codecApiPtr);
        }
    }
}
