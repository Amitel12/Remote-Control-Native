using System.Diagnostics;
using RemoteControl.Capture;
using RemoteControl.Codec;
using RemoteControl.Common;
using RemoteControl.Render;
using Vortice.Direct3D11;
using CodecColorConverter = RemoteControl.Codec.ColorConverter;

namespace RemoteControl.Tools.LoopbackHarness;

/// <summary>
/// Phase 0 entry point (see docs/PHASE-0.md): eventually wires
/// RemoteControl.Capture -> RemoteControl.Codec (encode) -> RemoteControl.Codec
/// (decode) -> RemoteControl.Render on one machine, no networking, and measures
/// whether the pipeline sustains 60fps 1080p with zero CPU-side texture copies.
///
/// Step 0 probes transforms, Step 1 tests the codec against a synthetic
/// texture, and Step 2 runs the full live desktop loopback.
/// </summary>
internal static class Program
{
    private const uint Width = 1920;
    private const uint Height = 1080;
    private const uint FpsNumerator = 60;
    private const uint FpsDenominator = 1;
    private const int FrameCount = 180; // 3s at 60fps -- Phase 0's target cadence, with enough frames for a stable latency average.

    [STAThread]
    private static int Main(string[] args)
    {
        var logger = new ConsoleLogger("LoopbackHarness");

        if (!RunStep0(logger))
            return 2;

        // MftProbe.Enumerate (Step 0) pairs its own MFStartup with an
        // unconditional MFShutdown in a finally block -- correct for Step 0
        // used standalone, but it leaves Media Foundation's DXVA/hardware
        // subsystem torn down for Step 1, which runs in the same process
        // immediately after. Every basic MF call (enumeration, type
        // negotiation, attribute get/set) kept working anyway, which is what
        // made this so hard to isolate -- only starting a real hardware
        // encode session actually needed the subsystem, and failed with the
        // misleading MF_E_UNSUPPORTED_D3D_TYPE ("input type is not
        // supported for D3D device") regardless of what the input sample
        // actually was. See docs/PHASE-0.md.
        Vortice.MediaFoundation.MediaFactory.MFStartup(false).CheckError();
        try
        {
            if (args.Contains("--step1"))
            {
                RunStep1(
                    logger,
                    verifyFrame: !args.Contains("--no-verify-frame"),
                    useNativeNvenc: !args.Contains("--mf-encoder"));
            }
            else
            {
                RunStep2(
                    logger,
                    verifyFrame: !args.Contains("--no-verify-frame"),
                    exerciseWindowState: args.Contains("--exercise-window-state"),
                    targetPresentedFrames: ReadFrameTarget(args));
            }
        }
        catch (Exception ex)
        {
            logger.Error("Phase 0 hardware run failed.", ex);
            return 1;
        }
        finally
        {
            Vortice.MediaFoundation.MediaFactory.MFShutdown();
        }

        return 0;
    }

    private static int ReadFrameTarget(string[] args)
    {
        var optionIndex = Array.IndexOf(args, "--frames");
        if (optionIndex < 0)
            return 300;

        if (optionIndex + 1 >= args.Length ||
            !int.TryParse(args[optionIndex + 1], out var frameTarget) ||
            frameTarget < 0)
        {
            throw new ArgumentException("--frames requires a non-negative integer. Use --frames 0 to run until the window is closed.");
        }

        return frameTarget;
    }

    private static void RunStep2(
        ILogger logger,
        bool verifyFrame,
        bool exerciseWindowState,
        int targetPresentedFrames)
    {
        while (true)
        {
            try
            {
                RunStep2Session(logger, verifyFrame, exerciseWindowState, targetPresentedFrames);
                return;
            }
            catch (DesktopConfigurationChangedException ex)
            {
                logger.Warn($"{ex.Message} Rebuilding capture, codec, decoder, and presenter for the new mode.");
            }
        }
    }

    private static void RunStep2Session(
        ILogger logger,
        bool verifyFrame,
        bool exerciseWindowState,
        int targetPresentedFrames)
    {
        Console.WriteLine();
        logger.Info("Phase 0 / Step 2 -- live desktop capture -> native NVENC -> D3D11 decode -> swap chain.");
        logger.Info(targetPresentedFrames == 0
            ? "Interactive/profiler mode: running until the presentation window is closed."
            : $"Close the presentation window to stop early; the acceptance run targets {targetPresentedFrames} presented frames.");
        Console.WriteLine();

        using var mfDevice = MfDevice.Create(logger);
        var displays = DisplayEnumerator.Enumerate(mfDevice.Device);
        if (displays.Count == 0)
            throw new InvalidOperationException("The D3D11 adapter has no attached desktop outputs.");

        foreach (var display in displays)
            logger.Info($"Output {display.OutputIndex}: {display.DeviceName}, {display.Width}x{display.Height} at ({display.Left},{display.Top}), {display.Rotation}.");

        var selected = displays[0];
        var presentationDisplay = displays.FirstOrDefault(display => display.OutputIndex != selected.OutputIndex) ?? selected;
        if (presentationDisplay.OutputIndex == selected.OutputIndex)
        {
            logger.Warn("Only one attached output is available; the presentation window will be captured and create visual feedback.");
        }
        else
        {
            logger.Info($"Presenting on output {presentationDisplay.OutputIndex} ({presentationDisplay.DeviceName}) " +
                        $"so output {selected.OutputIndex} capture does not include its own window.");
        }

        using var duplicator = new DesktopDuplicator(
            mfDevice.Device,
            mfDevice.ImmediateContext,
            selected.OutputIndex,
            logger);
        if ((duplicator.Width & 1) != 0 || (duplicator.Height & 1) != 0)
            throw new NotSupportedException($"NV12 requires even dimensions; selected output is {duplicator.Width}x{duplicator.Height}.");

        using var window = new PresentationWindow(presentationDisplay);
        using var presenter = new SwapChainPresenter(
            mfDevice.Device,
            mfDevice.ImmediateContext,
            window.Handle,
            duplicator.Width,
            duplicator.Height,
            window.ClientWidth,
            window.ClientHeight,
            logger);
        using var converter = new CodecColorConverter(mfDevice, duplicator.Width, duplicator.Height, logger: logger);
        using var encoder = new NvencEncoder(
            mfDevice,
            duplicator.Width,
            duplicator.Height,
            FpsNumerator,
            FpsDenominator,
            lowLatency: true,
            logger: logger);
        using var decoder = new HardwareDecoder(
            mfDevice,
            duplicator.Width,
            duplicator.Height,
            FpsNumerator,
            FpsDenominator,
            logger);

        TimeSpan? runLimit = targetPresentedFrames == 0
            ? null
            : TimeSpan.FromSeconds(Math.Max(20, targetPresentedFrames / 30.0));
        var run = Stopwatch.StartNew();
        var captureToPresentMs = new List<double>();
        var acquireTimeouts = 0;
        var captured = 0;
        var encoded = 0;
        var decoded = 0;
        var presented = 0;
        var occluded = 0;
        var skippedMinimized = 0;
        var verificationSaved = false;

        while (!window.IsClosed &&
               (targetPresentedFrames == 0 || presented < targetPresentedFrames) &&
               (runLimit is null || run.Elapsed < runLimit.Value))
        {
            window.PumpEvents();
            if (window.TryConsumeResize(out var width, out var height))
                presenter.Resize(width, height);

            if (!duplicator.TryAcquireNextFrame(100, out var desktopFrame))
            {
                acquireTimeouts++;
                continue;
            }

            using (desktopFrame)
            {
                captured++;
                if (exerciseWindowState)
                {
                    if (captured == 60)
                    {
                        logger.Info("[live] Exercising ResizeBuffers: resizing presentation client to 960x540.");
                        window.ResizeClient(960, 540);
                    }
                    else if (captured == 120)
                    {
                        logger.Info("[live] Exercising occlusion handling: minimizing presentation window.");
                        window.Minimize();
                    }
                    else if (captured == 150)
                    {
                        logger.Info("[live] Restoring presentation window after minimize.");
                        window.Restore();
                    }
                }

                var frameTimer = Stopwatch.StartNew();
                var sampleTime = (captured - 1L) * 10_000_000L * FpsDenominator / FpsNumerator;
                var sampleDuration = 10_000_000L * FpsDenominator / FpsNumerator;
                var (nv12, nv12Subresource) = converter.Convert(
                    desktopFrame!.Texture,
                    sampleTime,
                    sampleDuration);
                using (nv12)
                {
                    encoder.Encode(nv12, encodedBytes =>
                    {
                        encoded++;
                        decoder.Decode(encodedBytes, decodedFrame =>
                        {
                            using (decodedFrame.Texture)
                            {
                                decoded++;
                                if (verifyFrame && !verificationSaved)
                                {
                                    verificationSaved = true;
                                    var path = Path.Combine(AppContext.BaseDirectory, "step2-desktop-verify-frame.png");
                                    FrameVerifier.SaveNv12FrameAsPng(
                                        mfDevice.Device,
                                        mfDevice.ImmediateContext,
                                        decodedFrame.Texture,
                                        path,
                                        decodedFrame.SubresourceIndex);
                                    logger.Info($"Wrote live desktop verification frame: {path}");
                                }

                                var outcome = presenter.Present(decodedFrame.Texture, decodedFrame.SubresourceIndex);
                                switch (outcome)
                                {
                                    case PresentOutcome.Presented:
                                        presented++;
                                        frameTimer.Stop();
                                        captureToPresentMs.Add(frameTimer.Elapsed.TotalMilliseconds);
                                        break;
                                    case PresentOutcome.Occluded:
                                        occluded++;
                                        break;
                                    case PresentOutcome.SkippedWhileMinimized:
                                        skippedMinimized++;
                                        break;
                                }
                            }
                        });
                    }, nv12Subresource);
                }
            }
        }

        encoder.Drain(encodedBytes =>
        {
            encoded++;
            decoder.Decode(encodedBytes, decodedFrame =>
            {
                decoded++;
                decodedFrame.Texture.Dispose();
            });
        });
        decoder.Drain(decodedFrame =>
        {
            decoded++;
            decodedFrame.Texture.Dispose();
        });

        logger.Info($"[live] captured={captured}, encoded={encoded}, decoded={decoded}, presented={presented}, " +
                    $"timeouts={acquireTimeouts}, occluded={occluded}, minimized-skips={skippedMinimized}.");
        if (captureToPresentMs.Count > 5)
        {
            var steady = captureToPresentMs.Skip(5).ToList();
            logger.Info($"[live] capture-to-present callback latency avg={steady.Average():0.###}ms " +
                        $"min={steady.Min():0.###}ms max={steady.Max():0.###}ms " +
                        $"(n={steady.Count}, warmup skipped; Present called with syncInterval=0).");
        }

        if (targetPresentedFrames > 0 && !window.IsClosed && presented < targetPresentedFrames)
            throw new InvalidOperationException($"Live loop did not reach {targetPresentedFrames} presented frames within {runLimit!.Value.TotalSeconds:0}s (got {presented}).");

        if (!window.IsClosed)
            logger.Info("PASS -- sustained live capture/encode/decode/present loop completed on real hardware.");
        else
            logger.Warn($"Presentation window was closed after {presented} frames; this was an operator-shortened run.");
    }

    private static bool RunStep0(ILogger logger)
    {
        logger.Info("Phase 0 / Step 0 -- probing Media Foundation transforms.");
        logger.Info("See docs/PHASE-0.md for the build order, exit criteria and known landmines.");
        Console.WriteLine();

        IReadOnlyList<MftProbe.MftInfo> transforms;
        try
        {
            transforms = MftProbe.Enumerate(logger);
        }
        catch (Exception ex)
        {
            logger.Error("MFT probe failed outright. Media Foundation may be unavailable on this machine.", ex);
            return false;
        }

        if (transforms.Count == 0)
        {
            logger.Error("No video transforms found at all, hardware or software.");
            logger.Error("That is a genuine blocker for Phase 0 -- do not build the pipeline on this machine.");
            return false;
        }

        // "264" marks a name that looks like H.264. It is a hint for the reader,
        // not a load-bearing check -- Step 1 pins the format via IMFMediaType.
        Console.WriteLine($"  {"CATEGORY",-16} {"TYPE",-4} {"H264?",-6} NAME");
        Console.WriteLine($"  {new string('-', 16)} {new string('-', 4)} {new string('-', 6)} {new string('-', 40)}");
        foreach (var t in transforms)
        {
            Console.WriteLine($"  {t.Category,-16} {(t.IsHardware ? "HW" : "SW"),-4} " +
                              $"{(t.LooksLikeH264 ? "yes" : ""),-6} {t.FriendlyName}");
        }
        Console.WriteLine();

        var hwH264Encoders = transforms.Count(t => t.IsHardware && t.Category == "VideoEncoder" && t.LooksLikeH264);
        var hwH264Decoders = transforms.Count(t => t.IsHardware && t.Category == "VideoDecoder" && t.LooksLikeH264);
        var swH264Decoders = transforms.Count(t => !t.IsHardware && t.Category == "VideoDecoder" && t.LooksLikeH264);

        logger.Info($"Hardware H.264 encoders: {hwH264Encoders}");
        logger.Info($"Hardware H.264 decoders: {hwH264Decoders}");
        logger.Info($"Software H.264 decoders: {swH264Decoders}");
        Console.WriteLine();

        // The verdict deliberately keys on H.264 specifically, not on "any
        // hardware transform". A machine can expose a hardware MJPEG or HEVC
        // decoder and no hardware H.264 one at all, and counting those as a
        // pass is precisely the false green a go/no-go gate must never give.
        if (hwH264Encoders == 0)
        {
            logger.Warn("NO-GO on the encode side -- no hardware H.264 encoder MFT.");
            logger.Warn("Software encoding cannot meet Phase 0's latency goal. See docs/PHASE-0.md.");
            return false;
        }

        if (hwH264Decoders > 0)
        {
            logger.Info("PASS -- hardware H.264 encoder and decoder MFTs both present.");
            logger.Info("Step 1 (codec against a synthetic texture) is unblocked. This proves");
            logger.Info("presence, not that either transform can actually be driven.");
            return true;
        }

        if (swH264Decoders > 0)
        {
            // Expected on Windows rather than a fault: GPU vendors generally do
            // not ship a standalone hardware H.264 *decoder* MFT. Hardware
            // decode is reached through the Microsoft H264 Video Decoder MFT,
            // which uses DXVA2/D3D11VA internally once it is handed a D3D
            // device manager via MFT_MESSAGE_SET_D3D_MANAGER.
            logger.Info("PARTIAL -- hardware H.264 encoder present; no hardware H.264 decoder MFT.");
            logger.Info("That is normal on Windows. Vendors rarely expose a standalone hardware H.264");
            logger.Info("decoder; hardware decode goes through the Microsoft H264 Video Decoder MFT");
            logger.Info("with DXVA2/D3D11VA, enabled by MFT_MESSAGE_SET_D3D_MANAGER.");
            logger.Info("");
            logger.Info("Step 1 must therefore confirm that decoder reports MF_SA_D3D11_AWARE and");
            logger.Info("really returns D3D11 textures. Until then zero-copy decode is unproven.");
            return true;
        }

        logger.Warn("NO-GO on the decode side -- no H.264 decoder at all, hardware or software.");
        return false;
    }

    private static void RunStep1(ILogger logger, bool verifyFrame, bool useNativeNvenc)
    {
        Console.WriteLine();
        logger.Info("Phase 0 / Step 1 -- codec against a synthetic D3D11 texture (no capture, no swap chain).");
        logger.Info(useNativeNvenc
            ? "Encode path: NVIDIA native NVENCODE API (use --mf-encoder for the retained Media Foundation comparison)."
            : "Encode path: Media Foundation encoder MFT comparison.");
        Console.WriteLine();

        using var mfDevice = MfDevice.Create(logger);
        using var source = new SyntheticSource(mfDevice.Device, mfDevice.ImmediateContext, Width, Height);

        RunConfiguration(logger, mfDevice, source, lowLatency: true, verifyFrame, useNativeNvenc);
        RunConfiguration(logger, mfDevice, source, lowLatency: false, verifyFrame: false, useNativeNvenc);
    }

    private static void RunConfiguration(
        ILogger logger, MfDevice mfDevice, SyntheticSource source,
        bool lowLatency, bool verifyFrame, bool useNativeNvenc)
    {
        Console.WriteLine();
        logger.Info($"--- Configuration: {(lowLatency ? "low-latency IPPP" : "quality/default comparison IPPP")} ---");

        using var nvencEncoder = useNativeNvenc
            ? new NvencEncoder(mfDevice, Width, Height, FpsNumerator, FpsDenominator, lowLatency, logger: logger)
            : null;
        using var mfEncoder = useNativeNvenc
            ? null
            : new HardwareEncoder(mfDevice, Width, Height, FpsNumerator, FpsDenominator, lowLatency, logger: logger);
        using var colorConverter = new CodecColorConverter(mfDevice, Width, Height, logger: logger);
        using var decoder = new HardwareDecoder(mfDevice, Width, Height, FpsNumerator, FpsDenominator, logger);

        var encodeLatenciesMs = new List<double>();
        var decodeLatenciesMs = new List<double>();
        var compressedSizes = new List<int>();
        var framesDecoded = 0;
        var savedVerificationFrame = false;
        var sw = new Stopwatch();

        for (var i = 0; i < FrameCount; i++)
        {
            using var bgra = source.NextFrame();
            var (nv12, nv12Subresource) = colorConverter.Convert(
                bgra, i * 10_000_000L * FpsDenominator / FpsNumerator, 10_000_000L * FpsDenominator / FpsNumerator);
            using var nv12Owned = nv12;

            sw.Restart();
            void OnEncoded(byte[] encoded)
            {
                sw.Stop();
                encodeLatenciesMs.Add(sw.Elapsed.TotalMilliseconds);
                compressedSizes.Add(encoded.Length);

                var decodeSw = Stopwatch.StartNew();
                decoder.Decode(encoded, decoded =>
                {
                    decodeSw.Stop();
                    decodeLatenciesMs.Add(decodeSw.Elapsed.TotalMilliseconds);
                    framesDecoded++;

                    if (verifyFrame && !savedVerificationFrame)
                    {
                        savedVerificationFrame = true;
                        var path = Path.Combine(AppContext.BaseDirectory, "step1-verify-frame.png");
                        FrameVerifier.SaveNv12FrameAsPng(
                            mfDevice.Device, mfDevice.ImmediateContext, decoded.Texture, path, decoded.SubresourceIndex);
                        logger.Info($"Wrote verification frame: {path}");
                    }

                    decoded.Texture.Dispose();
                });
            }

            if (nvencEncoder is not null)
                nvencEncoder.Encode(nv12, OnEncoded, nv12Subresource);
            else
                mfEncoder!.Encode(mfDevice, nv12, OnEncoded, nv12Subresource);
        }

        void OnDrained(byte[] encoded)
        {
            compressedSizes.Add(encoded.Length);
            decoder.Decode(encoded, decoded => { framesDecoded++; decoded.Texture.Dispose(); });
        }

        if (nvencEncoder is not null)
            nvencEncoder.Drain(OnDrained);
        else
            mfEncoder!.Drain(OnDrained);
        decoder.Drain(decoded => { framesDecoded++; decoded.Texture.Dispose(); });

        var encoderLabel = nvencEncoder is not null
            ? "NATIVE NVIDIA NVENC (D3D11 input)"
            : mfEncoder!.UsingHardware
                ? "MEDIA FOUNDATION HARDWARE"
                : "MEDIA FOUNDATION SOFTWARE FALLBACK -- hardware MFT never accepted input, see docs/PHASE-0.md";
        var modeLabel = lowLatency ? "low-latency" : useNativeNvenc ? "quality-ippp" : "defaults";
        Report(logger, modeLabel, encoderLabel, encodeLatenciesMs, decodeLatenciesMs, compressedSizes, framesDecoded);
    }

    private static void Report(
        ILogger logger, string modeLabel, string encoderLabel,
        List<double> encodeLatenciesMs, List<double> decodeLatenciesMs, List<int> compressedSizes, int framesDecoded)
    {
        // Skip the first few frames: encoder/decoder pipelining means early
        // calls measure setup cost, not steady-state per-frame latency.
        const int warmup = 5;
        var steadyEncode = encodeLatenciesMs.Skip(warmup).ToList();
        var steadyDecode = decodeLatenciesMs.Skip(warmup).ToList();

        logger.Info($"[{modeLabel}] encoder: {encoderLabel}");
        logger.Info($"[{modeLabel}] frames encoded: {encodeLatenciesMs.Count}, decoded: {framesDecoded}");
        if (steadyEncode.Count > 0)
            logger.Info($"[{modeLabel}] encode latency avg={steadyEncode.Average():0.###}ms " +
                        $"min={steadyEncode.Min():0.###}ms max={steadyEncode.Max():0.###}ms (n={steadyEncode.Count}, warmup skipped)");
        if (steadyDecode.Count > 0)
            logger.Info($"[{modeLabel}] decode latency avg={steadyDecode.Average():0.###}ms " +
                        $"min={steadyDecode.Min():0.###}ms max={steadyDecode.Max():0.###}ms (n={steadyDecode.Count}, warmup skipped)");
        if (compressedSizes.Count > 0)
            logger.Info($"[{modeLabel}] avg compressed frame size: {compressedSizes.Average():0}B " +
                        $"({compressedSizes.Average() * 8 * FpsNumerator / FpsDenominator / 1_000_000.0:0.##}Mbps @ {FpsNumerator}fps)");
    }
}
