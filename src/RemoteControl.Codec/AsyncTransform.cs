using RemoteControl.Common;
using Vortice.MediaFoundation;

namespace RemoteControl.Codec;

/// <summary>
/// Drives one MFT correctly (see docs/PHASE-0.md, "Hardware MFTs are
/// asynchronous" -- and its Step 1 correction, twice over).
///
/// First correction: the Microsoft H264 decoder MFT turned out to be
/// synchronous despite D3D11/DXVA acceleration happening internally, unlike
/// the vendor encoder MFT which does report MF_TRANSFORM_ASYNC.
///
/// Second correction, found the hard way: for the async-reporting encoder,
/// the *documented* event-driven model (BeginGetEvent/EndGetEvent,
/// METransformNeedInput/METransformHaveOutput) never delivers a single
/// event on this driver -- confirmed with both the synchronous GetEvent(0)
/// (MSDN's documented legal substitute) and a proper callback-based
/// BeginGetEvent/EndGetEvent pump; both block forever. A working reference
/// implementation (sipsorcery/mediafoundationsamples' MFH264RoundTrip)
/// drives its hardware H.264 encoder MFT with GetInputStatus polling before
/// ProcessInput and a ProcessOutput polling loop after -- never touching
/// IMFMediaEventGenerator at all -- and that pattern is what actually works
/// here. MF_TRANSFORM_ASYNC_UNLOCK is still required (an MFT reporting
/// MF_TRANSFORM_ASYNC refuses most calls with MF_E_TRANSFORM_ASYNC_LOCKED
/// until unlocked), but the event queue it unlocks access to is simply not
/// used: GetInputStatus and ProcessOutput are called directly on both sync
/// and async transforms alike.
/// </summary>
public sealed class AsyncTransform : IDisposable
{
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(5);

    // MF_E_TRANSFORM_NEED_MORE_INPUT / MF_E_TRANSFORM_STREAM_CHANGE. Not
    // bound as named constants by Vortice; documented Win32 HRESULT values.
    private const int NeedMoreInputHResult = unchecked((int)0xC00D6D72);
    private const int StreamChangeHResult = unchecked((int)0xC00D6D61);

    public IMFTransform Transform { get; }
    public bool IsAsync { get; }

    private readonly ILogger _log;

    public AsyncTransform(IMFTransform transform, ILogger? logger = null)
    {
        Transform = transform;
        _log = logger ?? new ConsoleLogger(nameof(AsyncTransform));

        var attributes = transform.Attributes;
        IsAsync = attributes.GetUInt32(TransformAttributeKeys.TransformAsync, out var asyncFlag).Success && asyncFlag != 0;
        if (IsAsync)
        {
            // Required to unlock ProcessInput/ProcessOutput/etc at all on an
            // async-reporting MFT -- see class remarks for why nothing here
            // actually drives it through the event queue this unlocks.
            attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, true).CheckError();
        }
    }

    /// <summary>Best-effort MFT_MESSAGE_SET_D3D_MANAGER -- not every MFT accepts it.</summary>
    public void TrySetD3DManager(IMFDXGIDeviceManager manager)
    {
        try
        {
            Transform.ProcessMessage(TMessageType.MessageSetD3DManager, (UIntPtr)manager.NativePointer.ToInt64());
        }
        catch (Exception ex)
        {
            _log.Warn($"MFT_MESSAGE_SET_D3D_MANAGER rejected: {ex.Message}");
        }
    }

    public void BeginStreaming()
    {
        Transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        Transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
    }

    /// <summary>
    /// Feeds one input sample and drains every output sample the transform
    /// produces before it is ready for the next one, calling
    /// <paramref name="onOutput"/> for each.
    ///
    /// <paramref name="allocateOutput"/> is for transforms that don't set
    /// MFT_OUTPUT_STREAM_PROVIDES_SAMPLES and need a caller-supplied output
    /// sample of a specific kind -- the color converter must hand the Video
    /// Processor MFT a D3D11-texture-backed sample to render into, not the
    /// generic system-memory buffer <see cref="PullOutput"/> allocates by
    /// default (which is correct for the encoder's compressed byte output,
    /// but would silently break the color converter's zero-copy output).
    /// </summary>
    /// <param name="waitForInputAccept">
    /// Whether to poll GetInputStatus for MFT_INPUT_STATUS_ACCEPT_DATA before
    /// ProcessInput. Pass false when the caller already knows the transform
    /// is ready -- e.g. retrying immediately after a *rejected* ProcessInput
    /// call, which never actually consumed anything.
    /// </param>
    public void ProcessSample(
        int inputStreamId, int outputStreamId, IMFSample input, Action<IMFSample> onOutput,
        Func<IMFSample>? allocateOutput = null, bool waitForInputAccept = true)
    {
        if (waitForInputAccept) WaitForInputAccept(inputStreamId);
        Transform.ProcessInput(inputStreamId, input, 0);

        while (true)
        {
            var sample = PullOutput(outputStreamId, allocateOutput);
            if (sample is null) return; // MF_E_TRANSFORM_NEED_MORE_INPUT: ready for the next ProcessSample call.
            onOutput(sample);
            sample.Dispose();
        }
    }

    /// <summary>Flushes remaining output at end-of-stream (B-frame reordering, internal pipelining).</summary>
    public void Drain(int outputStreamId, Action<IMFSample> onOutput, Func<IMFSample>? allocateOutput = null)
    {
        Transform.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
        Transform.ProcessMessage(TMessageType.MessageCommandDrain, UIntPtr.Zero);

        while (true)
        {
            var sample = PullOutput(outputStreamId, allocateOutput);
            if (sample is null) return;
            onOutput(sample);
            sample.Dispose();
        }
    }

    private void WaitForInputAccept(int streamId)
    {
        var deadline = DateTime.UtcNow + ReadyTimeout;
        while (((InputStatusFlags)Transform.GetInputStatus(streamId) & InputStatusFlags.InputStatusAcceptData) == 0)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(
                    $"Timed out after {ReadyTimeout.TotalSeconds}s waiting for MFT_INPUT_STATUS_ACCEPT_DATA. " +
                    "See docs/PHASE-0.md landmines.");
            }
            Thread.Sleep(1);
        }
    }

    private IMFSample? PullOutput(int streamId, Func<IMFSample>? allocateOutput)
    {
        var outputInfo = Transform.GetOutputStreamInfo(streamId);
        var providesSamples = (outputInfo.Flags & (int)OutputStreamInfoFlags.OutputStreamProvidesSamples) != 0;

        IMFSample? allocated = null;
        if (!providesSamples)
        {
            if (allocateOutput is not null)
            {
                allocated = allocateOutput();
            }
            else
            {
                allocated = MediaFactory.MFCreateSample();
                using var buffer = MediaFactory.MFCreateMemoryBuffer(outputInfo.Size);
                allocated.AddBuffer(buffer);
            }
        }

        var dataBuffer = new OutputDataBuffer { StreamID = streamId, Sample = allocated };
        var result = Transform.ProcessOutput(ProcessOutputFlags.None, 1, ref dataBuffer, out _);

        if (result.Code == NeedMoreInputHResult)
        {
            allocated?.Dispose();
            return null;
        }

        if (result.Code == StreamChangeHResult)
        {
            allocated?.Dispose();
            using var newType = Transform.GetOutputAvailableType(streamId, 0);
            Transform.SetOutputType(streamId, newType, 0);
            return PullOutput(streamId, allocateOutput);
        }

        result.CheckError();
        return dataBuffer.Sample ?? allocated;
    }

    public void Dispose() => Transform.Dispose();
}
