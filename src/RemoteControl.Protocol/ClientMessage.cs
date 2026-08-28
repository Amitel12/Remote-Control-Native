using System.Text.Json.Serialization;

namespace RemoteControl.Protocol;

/// <summary>
/// Messages the C# app sends to the signaling server (WebSocket, JSON).
/// Mirrors the new payloads in packages/shared/src/signaling-protocol.ts
/// (this repo's docs/WIRE-PROTOCOL.md is the single source of truth both
/// repos point to). "type" is the wire discriminator, matching the old
/// TS union's tagged-union shape.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Register), "register")]
[JsonDerivedType(typeof(StunCandidates), "stun-candidates")]
[JsonDerivedType(typeof(HolePunchReady), "hole-punch-ready")]
public abstract record ClientMessage
{
    public sealed record Register(Role Role, string PairingCode) : ClientMessage;

    public sealed record StunCandidates(IReadOnlyList<CandidateInit> Candidates) : ClientMessage;

    public sealed record HolePunchReady : ClientMessage;
}
