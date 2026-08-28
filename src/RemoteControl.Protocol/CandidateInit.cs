namespace RemoteControl.Protocol;

/// <summary>Mirrors packages/shared's TS type of the same name in the old repo.</summary>
public enum CandidateKind
{
    Host,
    Srflx,
    Relay,
}

public sealed record CandidateInit(CandidateKind Kind, string Ip, int Port);
