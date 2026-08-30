using Vortice.MediaFoundation;

namespace RemoteControl.Codec;

/// <summary>
/// Finds and activates one MFT by category and the input/output types it
/// must actually support -- pinned via <see cref="RegisterTypeInfo"/> passed
/// straight to MFTEnumEx, not by matching friendly-name strings the way
/// <see cref="MftProbe"/> does for its read-only report. See
/// docs/PHASE-0.md: "Step 1 pins the format properly, via IMFMediaType on
/// the activated transform, which is where it actually matters."
/// </summary>
public static class MftFinder
{
    public static IMFTransform ActivateFirst(
        Guid category, bool hardware, RegisterTypeInfo? inputType, RegisterTypeInfo? outputType, string what)
    {
        var flags = (uint)((hardware ? EnumFlag.EnumFlagHardware : EnumFlag.EnumFlagSyncmft | EnumFlag.EnumFlagAsyncmft)
                            | EnumFlag.EnumFlagSortandfilter);

        using var activates = MediaFactory.MFTEnumEx(category, flags, inputType, outputType);
        foreach (var activate in activates)
        {
            using (activate)
            {
                return activate.ActivateObject<IMFTransform>();
            }
        }

        throw new InvalidOperationException(
            $"No {(hardware ? "hardware" : "software")} MFT found for {what} " +
            $"(category {category}). Re-run tools/LoopbackHarness's MftProbe step to confirm what this machine exposes.");
    }
}
