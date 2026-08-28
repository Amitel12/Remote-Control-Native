using System.Text.Json;
using RemoteControl.Protocol;
using Xunit;

namespace RemoteControl.Protocol.Tests;

public class ClientMessageJsonTests
{
    [Fact]
    public void Register_RoundTrips_And_UsesCamelCaseWireShape()
    {
        ClientMessage original = new ClientMessage.Register(Role.Host, "123456");

        var json = JsonSerializer.Serialize(original, ProtocolJson.Options);

        Assert.Contains("\"type\":\"register\"", json);
        Assert.Contains("\"role\":\"host\"", json);
        Assert.Contains("\"pairingCode\":\"123456\"", json);

        var roundTripped = JsonSerializer.Deserialize<ClientMessage>(json, ProtocolJson.Options);
        var register = Assert.IsType<ClientMessage.Register>(roundTripped);
        Assert.Equal(Role.Host, register.Role);
        Assert.Equal("123456", register.PairingCode);
    }

    [Fact]
    public void StunCandidates_RoundTrips_CandidateList()
    {
        ClientMessage original = new ClientMessage.StunCandidates(new[]
        {
            new CandidateInit(CandidateKind.Host, "192.168.1.5", 51820),
            new CandidateInit(CandidateKind.Srflx, "203.0.113.7", 51820),
        });

        var json = JsonSerializer.Serialize(original, ProtocolJson.Options);
        var roundTripped = JsonSerializer.Deserialize<ClientMessage>(json, ProtocolJson.Options);

        var candidates = Assert.IsType<ClientMessage.StunCandidates>(roundTripped);
        Assert.Equal(2, candidates.Candidates.Count);
        Assert.Equal(CandidateKind.Srflx, candidates.Candidates[1].Kind);
        Assert.Equal("203.0.113.7", candidates.Candidates[1].Ip);
    }

    [Fact]
    public void HolePunchReady_RoundTrips_WithNoFields()
    {
        ClientMessage original = new ClientMessage.HolePunchReady();

        var json = JsonSerializer.Serialize(original, ProtocolJson.Options);
        var roundTripped = JsonSerializer.Deserialize<ClientMessage>(json, ProtocolJson.Options);

        Assert.IsType<ClientMessage.HolePunchReady>(roundTripped);
    }

    [Fact]
    public void ServerMessage_Error_RoundTrips()
    {
        ServerMessage original = new ServerMessage.Error("register-failed", "A host is already registered for this pairing code.");

        var json = JsonSerializer.Serialize(original, ProtocolJson.Options);
        var roundTripped = JsonSerializer.Deserialize<ServerMessage>(json, ProtocolJson.Options);

        var error = Assert.IsType<ServerMessage.Error>(roundTripped);
        Assert.Equal("register-failed", error.Code);
    }

    [Fact]
    public void ServerMessage_PeerJoined_HasExpectedTypeDiscriminator()
    {
        ServerMessage original = new ServerMessage.PeerJoined();

        var json = JsonSerializer.Serialize(original, ProtocolJson.Options);

        Assert.Equal("{\"type\":\"peer-joined\"}", json);
    }
}
