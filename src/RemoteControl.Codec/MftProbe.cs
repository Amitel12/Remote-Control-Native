using RemoteControl.Common;
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
/// H.264 through MFTEnumEx's type-info arguments. Those arguments are passed
/// as null: the filter would need a Vortice struct whose exact name is not
/// pinned down here, and listing everything is at least as informative for a
/// probe -- the friendly names show plainly which codecs are present. Step 1
/// pins the format properly, via IMFMediaType on the activated transform,
/// which is where it actually matters.
/// </summary>
public static class MftProbe
{
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

        MediaFactory.MFStartup();
        try
        {
            results.AddRange(EnumerateCategory(
                "VideoEncoder", MFTransformCategoryGuids.VideoEncoder, log));
            results.AddRange(EnumerateCategory(
                "VideoDecoder", MFTransformCategoryGuids.VideoDecoder, log));
        }
        finally
        {
            MediaFactory.MFShutdown();
        }

        return results
            .OrderByDescending(r => r.IsHardware)
            .ThenBy(r => r.Category, StringComparer.Ordinal)
            .ThenBy(r => r.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<MftInfo> EnumerateCategory(
        string categoryName, Guid category, ILogger log)
    {
        // Hardware and software are enumerated separately rather than filtered
        // out of one combined list: MFT_ENUM_FLAG_HARDWARE is the only
        // authoritative signal that a transform is GPU-backed, and inferring it
        // from a friendly name is exactly the kind of guess this probe exists
        // to replace.
        foreach (var (flag, isHardware) in new[]
                 {
                     (MFTEnumFlag.Hardware, true),
                     (MFTEnumFlag.SyncMFT | MFTEnumFlag.AsyncMFT, false),
                 })
        {
            IMFActivate[] activates;
            try
            {
                activates = MediaFactory.MFTEnumEx(
                    category, flag | MFTEnumFlag.SortAndFilter, null, null);
            }
            catch (Exception ex)
            {
                log.Warn($"MFTEnumEx failed for {categoryName} " +
                         $"({(isHardware ? "hardware" : "software")}): {ex.Message}");
                continue;
            }

            foreach (var activate in activates)
            {
                string name;
                try
                {
                    name = activate.GetString(TransformAttributeKeys.MftFriendlyNameAttribute);
                }
                catch (Exception)
                {
                    // Some transforms genuinely carry no friendly name. Not a
                    // probe failure -- the entry still counts.
                    name = "(no friendly name)";
                }

                yield return new MftInfo(categoryName, name, isHardware);
                activate.Dispose();
            }
        }
    }
}
