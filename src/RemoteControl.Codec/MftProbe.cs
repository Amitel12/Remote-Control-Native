using RemoteControl.Common;
using Vortice.MediaFoundation;

namespace RemoteControl.Codec;

/// <summary>
/// Phase 0, Step 0 (see docs/PHASE-0.md): enumerate the Media Foundation
/// Transforms this machine actually exposes for H.264, before any pipeline is
/// built on the assumption that they exist.
///
/// This answers the two questions the rest of Phase 0 rests on:
/// (a) does this GPU expose *hardware* H.264 encoder and decoder MFTs, and
/// (b) does Vortice.MediaFoundation surface enough to drive them?
///
/// Deliberately read-only -- it enumerates and reports, it never activates or
/// configures a transform. Nothing here proves the pipeline works; it only
/// proves the pieces are present, which is the cheapest possible check of the
/// riskiest assumption in the rewrite.
/// </summary>
public static class MftProbe
{
    /// <summary>One enumerated transform.</summary>
    public sealed record MftInfo(string Category, string FriendlyName, bool IsHardware)
    {
        public override string ToString() =>
            $"{Category,-16} {(IsHardware ? "HW" : "SW")}  {FriendlyName}";
    }

    /// <summary>
    /// Enumerates H.264 encoder and decoder MFTs. Returns hardware entries
    /// first, since those are the ones Phase 0 needs; software entries are
    /// still reported because "only software present" is itself a finding
    /// worth seeing rather than an empty list.
    /// </summary>
    public static IReadOnlyList<MftInfo> Enumerate(ILogger? logger = null)
    {
        var log = logger ?? new ConsoleLogger(nameof(MftProbe));
        var results = new List<MftInfo>();

        MediaFactory.MFStartup();
        try
        {
            // Encoders: H.264 on the *output* side (raw frames in, H.264 out).
            results.AddRange(EnumerateCategory(
                "VideoEncoder",
                MFTransformCategoryGuids.VideoEncoder,
                inputType: null,
                outputType: new MFTRegisterTypeInfo(MediaTypeGuids.Video, VideoFormatGuids.H264),
                log));

            // Decoders: H.264 on the *input* side (H.264 in, raw frames out).
            results.AddRange(EnumerateCategory(
                "VideoDecoder",
                MFTransformCategoryGuids.VideoDecoder,
                inputType: new MFTRegisterTypeInfo(MediaTypeGuids.Video, VideoFormatGuids.H264),
                outputType: null,
                log));
        }
        finally
        {
            MediaFactory.MFShutdown();
        }

        return results
            .OrderByDescending(r => r.IsHardware)
            .ThenBy(r => r.Category)
            .ToList();
    }

    private static IEnumerable<MftInfo> EnumerateCategory(
        string categoryName,
        Guid category,
        MFTRegisterTypeInfo? inputType,
        MFTRegisterTypeInfo? outputType,
        ILogger log)
    {
        // Enumerate hardware and software separately rather than filtering a
        // combined list afterwards: MFT_ENUM_FLAG_HARDWARE is the only
        // authoritative signal that a transform is GPU-backed, and inferring
        // it from a friendly name is exactly the kind of guess this probe
        // exists to replace.
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
                    category,
                    flag | MFTEnumFlag.SortAndFilter,
                    inputType,
                    outputType);
            }
            catch (Exception ex)
            {
                log.Warn($"MFTEnumEx failed for {categoryName} ({(isHardware ? "hardware" : "software")}): {ex.Message}");
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
                    // Some transforms genuinely do not carry a friendly name.
                    // That is not a probe failure -- the entry still counts.
                    name = "(no friendly name)";
                }

                yield return new MftInfo(categoryName, name, isHardware);
                activate.Dispose();
            }
        }
    }
}
