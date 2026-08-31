using System.Net;
using System.Text;
using RemoteControl.Common;
using RemoteControl.Net.Transport;
using RemoteControl.Protocol;

namespace RemoteControl.Tools.LoopbackHarness;

/// <summary>
/// Real-hardware measurement of the redundant-send fix (docs/PHASE-3.md) --
/// real UDP sockets on loopback, real per-send loss simulation, no GPU or
/// window needed since this isolates the reliability mechanism itself:
/// sends "hello whatsup" as real KeyDown/KeyUp InputEvents under simulated
/// loss, once the old way (single send) and once the new way (redundant
/// send + sequence-gated dedup), and reconstructs what actually arrived on
/// each side -- directly answering "does this actually reduce garbling",
/// not just "does the wire format round-trip".
/// </summary>
internal static partial class Program
{
    private static void RunInputReliabilityDemo(ILogger logger)
    {
        const string text = "hello whatsup";
        const int lossPercent = 30;
        const int trialsPerCondition = 40;

        logger.Info($"Sending \"{text}\" ({text.Length} chars) as real KeyDown events over real loopback UDP sockets, " +
                    $"{lossPercent}% simulated per-send loss, {trialsPerCondition} trials per condition -- comparing average " +
                    "recovery rate, not single noisy runs (13 characters alone has too much run-to-run variance to trust one sample).");

        var withoutRates = new List<double>();
        var withRates = new List<double>();
        string? sampleWithout = null;
        string? sampleWith = null;
        for (var i = 0; i < trialsPerCondition; i++)
        {
            var without = RunTrial(text, lossPercent, redundant: false);
            var with = RunTrial(text, lossPercent, redundant: true);
            withoutRates.Add((double)without.Length / text.Length);
            withRates.Add((double)with.Length / text.Length);
            sampleWithout ??= without;
            sampleWith ??= with;
        }

        var avgWithout = withoutRates.Average();
        var avgWith = withRates.Average();
        logger.Info($"Sample single run without redundancy: \"{sampleWithout}\"");
        logger.Info($"Sample single run with redundancy:    \"{sampleWith}\"");
        logger.Info($"Average character-recovery rate over {trialsPerCondition} trials -- " +
                    $"without redundancy: {avgWithout:0.0%}, with redundancy: {avgWith:0.0%}.");

        var pass = avgWith > avgWithout;
        logger.Info(pass
            ? $"PASS -- redundant send recovered more text on average ({avgWith:0.0%} vs {avgWithout:0.0%}), " +
              "the statistically meaningful comparison (any single run can go either way -- see the two samples above)."
            : "FAIL -- redundancy did not measurably help on average across 40 trials each -- this would be a real finding, not just noise.");
    }

    private static string RunTrial(string text, int lossPercent, bool redundant)
    {
        using var senderSocket = new UdpTransport();
        senderSocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        using var receiverSocket = new UdpTransport();
        receiverSocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var receiverEndpoint = (IPEndPoint)receiverSocket.LocalEndPoint!;
        senderSocket.Connect(receiverEndpoint);

        const ulong sessionId = 1;
        var buffer = new byte[InputEventCodec.MaxSize];
        uint sequenceNumber = 0;

        void SendOnce(InputEvent inputEvent, uint seq)
        {
            if (Random.Shared.Next(100) < lossPercent)
                return;
            var length = InputEventCodec.Encode(inputEvent, buffer);
            senderSocket.Send(LanDatagramCodec.WrapInput(sessionId, seq, buffer.AsSpan(0, length)));
        }

        foreach (var rune in text.EnumerateRunes())
        {
            var down = new InputEvent.KeyDown(KeyKind.Character, (uint)rune.Value, ModifierKeys.None);
            var up = new InputEvent.KeyUp(KeyKind.Character, (uint)rune.Value, ModifierKeys.None);
            var downSeq = sequenceNumber++;
            var upSeq = sequenceNumber++;

            SendOnce(down, downSeq);
            SendOnce(up, upSeq);
            if (redundant)
            {
                Thread.Sleep(5); // stand-in for the real ~20ms stagger -- shortened so this trial finishes quickly.
                SendOnce(down, downSeq);
                SendOnce(up, upSeq);
            }
        }

        Thread.Sleep(50); // let everything arrive before draining.

        var reconstructed = new StringBuilder();
        var dedup = new InputSequenceDedup();
        var receiveBuffer = new byte[64];
        while (receiverSocket.Available > 0)
        {
            var received = receiverSocket.Receive(receiveBuffer);
            if (!LanDatagramCodec.TryRead(receiveBuffer.AsSpan(0, received), out var datagram) ||
                datagram.Kind != LanDatagramKind.Input)
            {
                continue;
            }

            var (seq, encoded) = LanDatagramCodec.ReadInput(datagram.Payload);
            if (!dedup.TryAccept(seq))
                continue; // duplicate copy -- same dedup as the real host (InputSequenceDedup).

            var decoded = InputEventCodec.Decode(encoded.Span);
            if (decoded is InputEvent.KeyDown(KeyKind.Character, var codePoint, _))
                reconstructed.Append(char.ConvertFromUtf32((int)codePoint));
        }

        return reconstructed.ToString();
    }
}
