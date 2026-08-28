using System.Net;
using RemoteControl.Net.Stun;
using Xunit;

namespace RemoteControl.Net.Tests.Stun;

public class StunMessageTests
{
    [Fact]
    public void BuildBindingRequest_HasBindingRequestTypeAndMagicCookie()
    {
        var (message, transactionId) = StunMessage.BuildBindingRequest();

        Assert.Equal(20, message.Length); // header only, no attributes
        Assert.Equal(0x00, message[0]);
        Assert.Equal(0x01, message[1]); // type 0x0001 = Binding Request
        Assert.Equal(0x21, message[4]);
        Assert.Equal(0x12, message[5]);
        Assert.Equal(0xA4, message[6]);
        Assert.Equal(0x42, message[7]); // magic cookie 0x2112A442
        Assert.Equal(StunMessage.TransactionIdLength, transactionId.Length);
    }

    [Fact]
    public void BuildBindingRequest_GeneratesDifferentTransactionIdsEachTime()
    {
        var (_, first) = StunMessage.BuildBindingRequest();
        var (_, second) = StunMessage.BuildBindingRequest();
        Assert.False(first.AsSpan().SequenceEqual(second));
    }

    /// <summary>
    /// Adapted from RFC 5769 section 2.2's official "Sample IPv4 Response"
    /// test vector (SOFTWARE + XOR-MAPPED-ADDRESS attributes only -- the
    /// real vector also carries MESSAGE-INTEGRITY and FINGERPRINT, which
    /// this codec deliberately doesn't parse or verify, so they're omitted
    /// here and the header's length field is adjusted accordingly). This
    /// is a real external reference vector, not a self-consistency check
    /// against this codec's own encoder -- it's the strongest test in this
    /// file for that reason. Expected decode per the RFC: 192.0.2.1:32853.
    /// </summary>
    [Fact]
    public void TryParseBindingResponse_DecodesRfc5769SampleIPv4Response()
    {
        byte[] message =
        {
            // Header: type=0x0101 (Binding Response), length=0x001c (28 bytes of attributes below)
            0x01, 0x01, 0x00, 0x1c,
            // Magic cookie
            0x21, 0x12, 0xa4, 0x42,
            // Transaction ID (12 bytes)
            0xb7, 0xe7, 0xa7, 0x01, 0xbc, 0x34, 0xd6, 0x86, 0xfa, 0x87, 0xdf, 0xae,

            // SOFTWARE attribute: type=0x8022, length=0x000b ("test vector", padded to 12 bytes)
            0x80, 0x22, 0x00, 0x0b,
            0x74, 0x65, 0x73, 0x74, 0x20, 0x76, 0x65, 0x63, 0x74, 0x6f, 0x72, 0x20,

            // XOR-MAPPED-ADDRESS attribute: type=0x0020, length=0x0008
            0x00, 0x20, 0x00, 0x08,
            0x00, 0x01, 0xa1, 0x47, // reserved=0, family=IPv4, xor'd port
            0xe1, 0x12, 0xa6, 0x43, // xor'd address
        };
        byte[] transactionId = { 0xb7, 0xe7, 0xa7, 0x01, 0xbc, 0x34, 0xd6, 0x86, 0xfa, 0x87, 0xdf, 0xae };

        var endpoint = StunMessage.TryParseBindingResponse(message, transactionId);

        Assert.NotNull(endpoint);
        Assert.Equal(IPAddress.Parse("192.0.2.1"), endpoint!.Address);
        Assert.Equal(32853, endpoint.Port);
    }

    [Fact]
    public void TryParseBindingResponse_RejectsMismatchedTransactionId()
    {
        var (_, transactionId) = StunMessage.BuildBindingRequest();
        var response = BuildSyntheticIPv4Response(transactionId, IPAddress.Parse("203.0.113.7"), 40000);

        var wrongTransactionId = new byte[StunMessage.TransactionIdLength];
        Array.Fill(wrongTransactionId, (byte)0xFF);

        var result = StunMessage.TryParseBindingResponse(response, wrongTransactionId);

        Assert.Null(result);
    }

    [Fact]
    public void TryParseBindingResponse_RejectsNonResponseMessageType()
    {
        var (request, transactionId) = StunMessage.BuildBindingRequest();
        Assert.Null(StunMessage.TryParseBindingResponse(request, transactionId));
    }

    [Fact]
    public void TryParseBindingResponse_RejectsTruncatedMessage_WithoutThrowing()
    {
        byte[] tooShort = { 0x01, 0x01, 0x00 };
        Assert.Null(StunMessage.TryParseBindingResponse(tooShort, ReadOnlySpan<byte>.Empty));
    }

    [Theory]
    [InlineData("192.168.1.42", 51820)]
    [InlineData("8.8.8.8", 65535)]
    [InlineData("0.0.0.0", 1)]
    public void RoundTrips_SyntheticResponses_ForVariousAddressesAndPorts(string ip, int port)
    {
        var (_, transactionId) = StunMessage.BuildBindingRequest();
        var response = BuildSyntheticIPv4Response(transactionId, IPAddress.Parse(ip), port);

        var endpoint = StunMessage.TryParseBindingResponse(response, transactionId);

        Assert.NotNull(endpoint);
        Assert.Equal(IPAddress.Parse(ip), endpoint!.Address);
        Assert.Equal(port, endpoint.Port);
    }

    /// <summary>
    /// Hand-rolled independently of StunMessage's own parsing code (this is
    /// a from-scratch byte-level construction, not a call into the class
    /// under test) so the round-trip tests above are a genuine two-way
    /// check, not just testing the parser against its own encoder.
    /// </summary>
    private static byte[] BuildSyntheticIPv4Response(byte[] transactionId, IPAddress address, int port)
    {
        const uint magicCookie = 0x2112A442;
        var addressBytes = address.GetAddressBytes();

        var xorPort = (ushort)(port ^ (magicCookie >> 16));
        var xorAddress = new byte[4];
        var cookieBytes = new byte[] { 0x21, 0x12, 0xa4, 0x42 };
        for (var i = 0; i < 4; i++) xorAddress[i] = (byte)(addressBytes[i] ^ cookieBytes[i]);

        var message = new List<byte>();
        message.AddRange(new byte[] { 0x01, 0x01, 0x00, 0x0c }); // type=Binding Response, length=12 (one attribute)
        message.AddRange(new byte[] { 0x21, 0x12, 0xa4, 0x42 }); // magic cookie
        message.AddRange(transactionId);

        message.AddRange(new byte[] { 0x00, 0x20, 0x00, 0x08 }); // XOR-MAPPED-ADDRESS, length 8
        message.Add(0x00); // reserved
        message.Add(0x01); // family = IPv4
        message.Add((byte)(xorPort >> 8));
        message.Add((byte)xorPort);
        message.AddRange(xorAddress);

        return message.ToArray();
    }
}
