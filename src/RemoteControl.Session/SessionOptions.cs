namespace RemoteControl.Session;

public sealed record SessionOptions
{
    public int TargetFrames { get; init; }
    public int ParityPercent { get; init; }
    public bool AdaptiveBitrate { get; init; }
    public bool AdaptiveFec { get; init; }
    public bool IntraRefresh { get; init; }
    public bool RemoteInput { get; init; } = true;
    public uint BitrateBps { get; init; } = 8_000_000;
    public uint OutputIndex { get; init; }
    public uint FpsNumerator { get; init; } = 60;
    public uint FpsDenominator { get; init; } = 1;
    public int DropPercent { get; init; }
    public int DropInputPercent { get; init; }
    public bool VerifyFrame { get; init; }
}

public readonly record struct SessionStats(int Frames, double Fps, double RttMs, double LossRate, uint BitrateBps);
