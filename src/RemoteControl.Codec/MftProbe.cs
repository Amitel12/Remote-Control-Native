using System.Runtime.InteropServices;
using RemoteControl.Common;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace RemoteControl.Codec;

/// <summary>
/// Phase 0, Step 0 (see docs/PHASE-0.md): enumerate the Media Foundation
/// Transforms this machine actually exposes, before any pipeline is built on
/// the assumption that they exist.
///
/// This answers the two questions the rest of Phase 0 rests on:
/// (a) does this GPU expose *hardware* video encoder and decoder MFTs, and
/// (b) does Vortice.MediaFoundation surface enough to drive them?
///
/// Deliberately read-only -- it enumerates and reports, it never activates or
/// configures a transform. Nothing here proves the pipeline works; it only
/// proves the pieces are present, which is the cheapest possible check of the
/// riskiest assumption in the rewrite.
///
/// Note it enumerates *all* video encoders/decoders rather than filtering to
/// H.264 through MFTEnumEx's type-info arguments, which are passed as null.
/// Listing everything is at least as informative for a probe -- the friendly
/// names show plainly which codecs are present. Step 1 pins the format
/// properly, via IMFMediaType on the activated transform, which is where it
/// actually matters.
/// </summary>
public static class MftProbe
{
    /// <summary>
    /// MFT_FRIENDLY_NAME_Attribute. Inlined as a raw GUID rather than taken
    /// from Vortice's TransformAttributeKeys, because that field may be a
    /// MediaAttributeKey wrapper rather than a Guid and GetAllocatedString
    /// wants a Guid. The value is a fixed, documented Win32 constant.
    /// </summary>
    private static readonly Guid MftFriendlyNameAttribute =
        new("314FFBAE-5B41-4C95-9C19-4E7D586FACE3");

    /// <summary>
    /// MF_VERSION, as passed to MFStartup. Inlined for the same reason: the
    /// value is fixed and this avoids depending on the exact type of
    /// MediaFactory.Version.
    /// </summary>
    private const uint MfVersion = 0x00020070;

    /// <summary>One enumerated transform.</summary>
    public sealed record MftInfo(string Category, string FriendlyName, bool IsHardware)
    {
        /// <summary>
        /// Whether the friendly name looks like an H.264 transform. A hint for
        /// the human reading the table, not a load-bearing check -- the
        /// authoritative answer comes from setting media types in Step 1.
        /// </summary>
        public bool LooksLikeH264 =>
            FriendlyName.Contains("264", StringComparison.OrdinalIgnoreCase) ||
            FriendlyName.Contains("AVC", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Enumerates video encoder and decoder MFTs, hardware entries first --
    /// those are the ones Phase 0 needs. Software entries are still reported,
    /// because "only software present" is itself a finding worth seeing rather
    /// than an empty list.
    /// </summary>
    public static IReadOnlyList<MftInfo> Enumerate(ILogger? logger = null)
    {
        var log = logger ?? new ConsoleLogger(nameof(MftProbe));
        var results = new List<MftInfo>();

        // Vortice binds no MFShutdown, so this startup is deliberately not
        // paired with one. Harmless here: the probe is a short-lived process
        // and the reference goes away when it exits.
        MediaFactory.MFStartup(MfVersion, 0);

        results.AddRange(EnumerateCategory(
            "VideoEncoder", TransformCategoryGuids.VideoEncoder, log));
        results.AddRange(EnumerateCategory(
            "VideoDecoder", TransformCategoryGuids.VideoDecoder, log));

        return results
            .OrderByDescending(r => r.IsHardware)
            .ThenBy(r => r.Category, StringComparer.Ordinal)
            .ThenBy(r => r.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<MftInfo> EnumerateCategory(
        string categoryName, Guid category, ILogger log)
    {
        var found = new List<MftInfo>();

        // Hardware and software are enumerated separately rather than filtered
        // out of one combined list: MFT_ENUM_FLAG_HARDWARE is the only
        // authoritative signal that a transform is GPU-backed, and inferring it
        // from a friendly name is exactly the kind of guess this probe exists
        // to replace.
        foreach (var (flag, isHardware) in new[]
                 {
                     (EnumFlag.EnumFlagHardware, true),
                     (EnumFlag.EnumFlagSyncmft | EnumFlag.EnumFlagAsyncmft, false),
                 })
        {
            var flags = (uint)(flag | EnumFlag.EnumFlagSortandfilter);

            IntPtr activateArray;
            uint count;
            try
            {
                // MFTEnumEx hands back a CoTaskMem array of IMFActivate*, not a
                // managed array -- hence the manual walk and free below.
                MediaFactory.MFTEnumEx(category, flags, null, null, out activateArray, out count);
            }
            catch (Exception ex)
            {
                log.Warn($"MFTEnumEx failed for {categoryName} " +
                         $"({(isHardware ? "hardware" : "software")}): {ex.Message}");
                continue;
            }

            if (activateArray == IntPtr.Zero || count == 0)
            {
                if (activateArray != IntPtr.Zero) Marshal.FreeCoTaskMem(activateArray);
                continue;
            }

            try
            {
                for (var i = 0; i < (int)count; i++)
                {
                    var ptr = Marshal.ReadIntPtr(activateArray, i * IntPtr.Size);
                    if (ptr == IntPtr.Zero) continue;

                    var activate = MarshallingHelpers.FromPointer<IMFActivate>(ptr);
                    if (activate is null) continue;

                    string name;
                    try
                    {
                        name = activate.GetAllocatedString(MftFriendlyNameAttribute);
                    }
                    catch (Exception)
                    {
                        // Some transforms genuinely carry no friendly name. Not
                        // a probe failure -- the entry still counts.
                        name = "(no friendly name)";
                    }

                    found.Add(new MftInfo(categoryName, name, isHardware));
                    activate.Dispose();
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(activateArray);
            }
        }

        return found;
    }
}
