using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemoteControl.Protocol;

/// <summary>
/// Central JSON options for the signaling channel only -- camelCase property
/// names and lowercase enum values to match the wire format the TS signaling
/// server speaks (packages/shared/src/signaling-protocol.ts in the old
/// repo). Deliberately runtime-reflection-based rather than a
/// source-generated JsonSerializerContext for now (simpler to get right for
/// the tagged-union ClientMessage/ServerMessage shapes); revisit for
/// AOT/perf if this ever becomes a hot path -- it isn't, signaling is
/// low-frequency by design. NOT used for the binary UDP wire structs (see
/// WireStructs.cs) -- those never touch JSON at all.
/// </summary>
public static class ProtocolJson
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
