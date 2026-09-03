using RemoteControl.Capture;
using RemoteControl.Codec;
using RemoteControl.Common;
using RemoteControl.Net.Transport;
using RemoteControl.Net.Video;
using RemoteControl.Protocol;
using RemoteControl.Render;

namespace RemoteControl.Session;

public sealed class ClientVideoSession : IDisposable
{
    // How many completed-but-out-of-order frames to hold before giving up on the missing
    // next-expected one and decoding ahead anyway -- bounded so a genuinely lost frame (not
    // just reordered) can't stall the pipeline forever. Small on purpose, matching FramePacer's
    // "near-zero, adaptive, not deep" jitter-buffer philosophy (see docs/ARCHITECTURE.md) --
    // this just applies that same philosophy to decode *order*, not only timing.
    //
    // Adaptive (docs/PHASE-4.md): starts at the floor and only grows when reordering
    // actually forces a skip -- fast growth (real reordering is bursty, one miss often means
    // more coming) and slow shrink (only after a long quiet streak) mirror the same AIMD
    // shape CongestionController already uses for bitrate, just for window size instead.
    private const int ReorderWindowFloor = 4;
    private const int ReorderWindowCeiling = 32;
    private const int QuietFramesBeforeShrink = 300;
    private int _reorderWindowFrames = ReorderWindowFloor;
    private int _framesSinceLastForcedSkip;

    private readonly HardwareDecoder _decoder;
    private readonly SwapChainPresenter _presenter;
    private readonly VideoDepacketizer _depacketizer = new();
    private readonly SortedDictionary<uint, byte[]> _pendingDecode = new();
    private readonly MfDevice _mfDevice;
    private readonly ILogger _logger;
    private readonly bool _verifyFrame;
    private bool _verificationSaved;
    private bool _drained;
    private uint _nextFrameIndexToDecode;
    private bool _sawFirstFrame;
    public int SkippedForReordering { get; private set; }
    public int SkippedForStalePresent { get; private set; }
    public int ReorderWindowFrames => _reorderWindowFrames;

    public ulong SessionId { get; }
    public int CompletedFrames { get; private set; }
    public int Decoded { get; private set; }
    public int Presented { get; private set; }
    public int IncompleteFrames => _depacketizer.InProgressFrameCount;
    public int DroppedIncompleteFrames => _depacketizer.DroppedIncompleteFrameCount;

    public ClientVideoSession(
        MfDevice mfDevice,
        nint windowHandle,
        uint clientWidth,
        uint clientHeight,
        LanDatagram configuration,
        bool verifyFrame,
        ILogger logger)
    {
        _mfDevice = mfDevice;
        _logger = logger;
        _verifyFrame = verifyFrame;
        SessionId = configuration.SessionId;
        SwapChainPresenter? presenter = null;
        HardwareDecoder? decoder = null;
        try
        {
            presenter = new SwapChainPresenter(
                mfDevice.Device,
                mfDevice.ImmediateContext,
                windowHandle,
                configuration.Width,
                configuration.Height,
                clientWidth,
                clientHeight,
                logger);
            decoder = new HardwareDecoder(
                mfDevice,
                configuration.Width,
                configuration.Height,
                configuration.FpsNumerator,
                configuration.FpsDenominator,
                logger);
        }
        catch
        {
            decoder?.Dispose();
            presenter?.Dispose();
            throw;
        }

        _presenter = presenter;
        _decoder = decoder;
        logger.Info(
            $"LAN video session {SessionId:X16} configured for " +
            $"{configuration.Width}x{configuration.Height}@" +
            $"{(double)configuration.FpsNumerator / configuration.FpsDenominator:0.##}fps.");
    }

    public bool TryProcessVideoPacket(ReadOnlySpan<byte> packet)
    {
        VideoPacketHeader header;
        byte[]? encodedFrame;
        try
        {
            header = VideoPacketHeader.ReadFrom(packet);
            encodedFrame = _depacketizer.AddPacket(packet);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }

        if (encodedFrame is null)
            return true;

        CompletedFrames++;
        EnqueueForOrderedDecode(header.FrameIndex, encodedFrame);
        return true;
    }

    /// <summary>
    /// A frame finishing reassembly doesn't mean it's safe to decode yet -- network reordering
    /// (real, not just per-shard loss; see docs/PHASE-4.md) can let a later frame's shards all
    /// arrive and complete before an earlier frame's do. Feeding H.264 IPPP frames to the decoder
    /// out of temporal order corrupts its reference-frame state, so this holds completed frames
    /// until they can be released in strictly increasing frame-index order.
    /// </summary>
    private void EnqueueForOrderedDecode(uint frameIndex, byte[] encodedFrame)
    {
        if (!_sawFirstFrame)
        {
            _sawFirstFrame = true;
            _nextFrameIndexToDecode = frameIndex;
        }

        if (frameIndex < _nextFrameIndexToDecode)
            return; // already released past this index -- the depacketizer's own watermark should prevent this, but never decode backwards regardless.

        _pendingDecode[frameIndex] = encodedFrame;

        while (_pendingDecode.Count >= _reorderWindowFrames && !_pendingDecode.ContainsKey(_nextFrameIndexToDecode))
        {
            SkippedForReordering++;
            _nextFrameIndexToDecode++;
            // The window was too tight to hold this reorder -- grow it fast (real jitter is
            // bursty, one miss often means more coming) rather than waiting to see if it
            // happens again.
            _reorderWindowFrames = Math.Min(ReorderWindowCeiling, _reorderWindowFrames + 4);
            _framesSinceLastForcedSkip = 0;
        }

        while (_pendingDecode.Remove(_nextFrameIndexToDecode, out var next))
        {
            _nextFrameIndexToDecode++;
            if (++_framesSinceLastForcedSkip >= QuietFramesBeforeShrink)
            {
                _reorderWindowFrames = Math.Max(ReorderWindowFloor, _reorderWindowFrames - 1);
                _framesSinceLastForcedSkip = 0;
            }
            // Another frame is already buffered right behind this one, so this one will be
            // stale the instant it's shown -- still decode it (H.264 IPPP needs every frame
            // decoded in order to keep the reference chain valid for the ones after it) but
            // don't present it, so a backlog after a network stall snaps straight to the
            // newest available frame instead of visibly replaying the whole gap.
            if (_pendingDecode.ContainsKey(_nextFrameIndexToDecode))
            {
                SkippedForStalePresent++;
                _decoder.Decode(next, DiscardDecoded);
            }
            else
            {
                _decoder.Decode(next, Present);
            }
        }
    }

    private static void DiscardDecoded(DecodedFrame decodedFrame) => decodedFrame.Texture.Dispose();

    public void Resize(uint width, uint height) => _presenter.Resize(width, height);

    public void Drain()
    {
        if (_drained)
            return;
        _drained = true;
        _decoder.Drain(Present);
    }

    private void Present(DecodedFrame decodedFrame)
    {
        using (decodedFrame.Texture)
        {
            Decoded++;
            if (_verifyFrame && !_verificationSaved)
            {
                _verificationSaved = true;
                var path = Path.Combine(AppContext.BaseDirectory, "phase1-lan-client-verify-frame.png");
                FrameVerifier.SaveNv12FrameAsPng(
                    _mfDevice.Device,
                    _mfDevice.ImmediateContext,
                    decodedFrame.Texture,
                    path,
                    decodedFrame.SubresourceIndex);
                _logger.Info($"Wrote LAN client verification frame: {path}");
            }

            if (_presenter.Present(decodedFrame.Texture, decodedFrame.SubresourceIndex) == PresentOutcome.Presented)
                Presented++;
        }
    }

    public void Dispose()
    {
        _decoder.Dispose();
        _presenter.Dispose();
    }
}
