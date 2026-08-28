namespace RemoteControl.Protocol;

/// <summary>
/// Serialized as lowercase "host"/"client" on the wire -- see
/// <see cref="ProtocolJson.Options"/>, which registers the naming-policy-aware
/// string converter centrally rather than per-enum (a per-type
/// [JsonConverter] attribute can't be given a naming policy argument).
/// </summary>
public enum Role
{
    Host,
    Client,
}
