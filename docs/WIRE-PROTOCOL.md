# Wire protocol

Single source of truth for the two protocols this app speaks, referenced
from both this repo and `amitel12/tests` (the signaling server + the
retired Electron prototype). Keep this file in sync with both
implementations when either changes.

## 1. Signaling protocol (JSON over WebSocket)

Spoken between `RemoteControl.Signaling.SignalingClient` (this repo) and
`packages/signaling-server` (the `amitel12/tests` repo, unchanged
room/relay logic). Types: `RemoteControl.Protocol.ClientMessage` /
`ServerMessage` here, `packages/shared/src/signaling-protocol.ts` there.

Property names are camelCase; the `type` field is the tagged-union
discriminator; enum values are lowercase.

**Client -> Server**
| type | fields | purpose |
|---|---|---|
| `register` | `role` (`"host"` \| `"client"`), `pairingCode` | join/create a room |
| `stun-candidates` | `candidates: CandidateInit[]` | this peer's local + server-reflexive candidates |
| `hole-punch-ready` | (none) | "I've started sending hole-punch packets, you should be receiving them" |

**Server -> Client**
| type | fields | purpose |
|---|---|---|
| `registered` | `pairingCode`, `role` | ack of `register` |
| `peer-joined` | (none) | the other side of the room just registered |
| `peer-left` | (none) | the other side's socket closed |
| `stun-candidates` | `candidates: CandidateInit[]` | relayed from the peer |
| `hole-punch-ready` | (none) | relayed from the peer |
| `error` | `code`, `message` | e.g. `register-failed` |

`CandidateInit = { kind: "host" | "srflx" | "relay", ip: string, port: number }`.

This is what replaced the old SDP-offer/SDP-answer/ICE-candidate exchange
-- the signaling server's own relay logic (`rooms.ts`) never needed to
change, only these payload shapes and `server.ts`'s one `switch` case.

## 2. Media/input transport (binary, custom UDP -- not this protocol's job to describe the transport itself, only its payloads)

Rides ENet-CSharp channels once the peer connection (direct P2P via
hole-punch, or via the coturn TURN relay) is established. Never JSON --
this is the latency-sensitive hot path.

### Video channel (unreliable/unordered, ENet)

Every UDP payload is `VideoPacketHeader` (15 bytes, little-endian,
`RemoteControl.Protocol.VideoPacketHeader`) followed by one FEC shard's
bytes:

```
[0..4)   FrameIndex        uint32
[4..6)   FecShardIndex     uint16   this shard's index within its FEC block (0..FecTotalShards-1)
[6..8)   FecDataShards     uint16   K
[8..10)  FecTotalShards    uint16   N (N-K = recoverable losses)
[10]     Flags             byte     bit0=StartOfFrame bit1=EndOfFrame bit2=IsParityShard
[11..15) FrameByteLength   uint32   total original encoded frame length (same on every shard of the frame)
```

Reed-Solomon erasure coding over GF(256) (`RemoteControl.Net.Fec.ReedSolomonCodec`)
produces the N shards from K data shards; any K of the N reconstruct the
frame with no retransmission. See `RemoteControl.Net.Video.VideoPacketizer`/
`VideoDepacketizer`.

### Input channels (ENet)

Two channels, mirroring the old app's split: reliable-ordered for
mousedown/up/wheel/key (a lost mouseup must never happen -- stuck button),
unreliable-unordered for mousemove (only the latest position matters).
Payloads are `RemoteControl.Protocol.InputEvent` encoded via
`InputEventCodec` (7-10 bytes per event depending on type, tag byte +
fixed fields, see `InputEvent.cs` for the exact layout per event type).

Key encoding carries `KeyKind` (`Character` = literal Unicode code point,
type via `SendInput`+`KEYEVENTF_UNICODE`; `Named` = Enter/Backspace/arrows/
modifiers, needs real hold semantics) plus currently-held `ModifierKeys` --
see `docs/ARCHITECTURE.md` lesson #2 in the `amitel12/tests` repo for why
this split exists.

### Control channel (reliable, ENet)

Not yet defined in code -- ported from the old app's `ControlMessage`
union (`list-displays-response`, `select-display`, `remote-input-suppressed`/
`-resumed`, `session-ending`) when Phase 5 (feature parity pass) lands.
Update this section then.
