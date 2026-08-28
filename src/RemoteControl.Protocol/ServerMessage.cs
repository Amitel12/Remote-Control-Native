using System.Text.Json.Serialization;

namespace RemoteControl.Protocol;

/// <summary>Messages the signaling server sends back. See ClientMessage.cs.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Registered), "registered")]
[JsonDerivedType(typeof(PeerJoined), "peer-joined")]
[JsonDerivedType(typeof(PeerLeft), "peer-left")]
[JsonDerivedType(typeof(StunCandidates), "stun-candidates")]
[JsonDerivedType(typeof(HolePunchReady), "hole-punch-ready")]
[JsonDerivedType(typeof(Error), "error")]
public abstract record ServerMessage
{
    public sealed record Registered(string PairingCode, Role Role) : ServerMessage;

    public sealed record PeerJoined : ServerMessage;

    public sealed record PeerLeft : ServerMessage;

    public sealed record StunCandidates(IReadOnlyList<CandidateInit> Candidates) : ServerMessage;

    public sealed record HolePunchReady : ServerMessage;

    public sealed record Error(string Code, string Message) : ServerMessage;
}
