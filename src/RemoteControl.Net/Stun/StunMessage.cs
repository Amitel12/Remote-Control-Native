using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace RemoteControl.Net.Stun;

/// <summary>
/// Minimal RFC 5389 STUN client codec: builds a Binding Request and parses
/// a Binding Response's XOR-MAPPED-ADDRESS attribute to discover this
/// host's server-reflexive (public) address/port. This is the "S" of the
/// hand-rolled simplified-ICE NAT traversal design in
/// docs/ARCHITECTURE.md's Phase 2 -- deliberately not a full STUN/TURN
/// client library, just the one request/response shape actually needed.
/// Works against any RFC 5389-compliant STUN server, including coturn's
/// STUN listener.
/// </summary>
public static class StunMessage
{
    private const ushort BindingRequestType = 0x0001;
    private const ushort BindingResponseType = 0x0101;
    private const ushort BindingErrorResponseType = 0x0111;
    private const uint MagicCookie = 0x2112A442;
    private const ushort XorMappedAddressAttribute = 0x0020;
    private const ushort MappedAddressAttribute = 0x0001; // RFC 3489 legacy, some servers only send this
    public const int TransactionIdLength = 12;
    private const int HeaderLength = 20;

    /// <summary>Builds a Binding Request with a fresh random transaction ID, returned alongside the message bytes so the caller can match the eventual response.</summary>
    public static (byte[] Message, byte[] TransactionId) BuildBindingRequest()
    {
        var transactionId = new byte[TransactionIdLength];
        Random.Shared.NextBytes(transactionId);

        var message = new byte[HeaderLength];
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(0, 2), BindingRequestType);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(2, 2), 0); // no attributes
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(4, 4), MagicCookie);
        transactionId.CopyTo(message.AsSpan(8, TransactionIdLength));

        return (message, transactionId);
    }

    /// <summary>
    /// Parses a Binding Response (or Error Response) and, on success,
    /// returns the server-reflexive endpoint from its XOR-MAPPED-ADDRESS
    /// attribute (falling back to the legacy non-XOR MAPPED-ADDRESS if
    /// that's what the server sent). Returns null for anything that isn't
    /// a successful Binding Response matching the expected transaction ID
    /// -- malformed/unexpected datagrams are common on a raw UDP socket
    /// (stray traffic, retransmitted stale responses) and should be
    /// ignored, not thrown on.
    /// </summary>
    public static IPEndPoint? TryParseBindingResponse(ReadOnlySpan<byte> message, ReadOnlySpan<byte> expectedTransactionId)
    {
        if (message.Length < HeaderLength) return null;

        var type = BinaryPrimitives.ReadUInt16BigEndian(message[0..2]);
        if (type != BindingResponseType) return null; // includes silently ignoring BindingErrorResponseType

        var attributesLength = BinaryPrimitives.ReadUInt16BigEndian(message[2..4]);
        var magicCookie = BinaryPrimitives.ReadUInt32BigEndian(message[4..8]);
        if (magicCookie != MagicCookie) return null;

        var transactionId = message.Slice(8, TransactionIdLength);
        if (!transactionId.SequenceEqual(expectedTransactionId)) return null;

        if (HeaderLength + attributesLength > message.Length) return null; // truncated datagram

        var attributes = message.Slice(HeaderLength, attributesLength);
        var xorResult = FindAddressAttribute(attributes, XorMappedAddressAttribute, transactionId, xor: true);
        if (xorResult is not null) return xorResult;

        return FindAddressAttribute(attributes, MappedAddressAttribute, transactionId, xor: false);
    }

    private static IPEndPoint? FindAddressAttribute(ReadOnlySpan<byte> attributes, ushort wantedType, ReadOnlySpan<byte> transactionId, bool xor)
    {
        var offset = 0;
        while (offset + 4 <= attributes.Length)
        {
            var attrType = BinaryPrimitives.ReadUInt16BigEndian(attributes.Slice(offset, 2));
            var attrLength = BinaryPrimitives.ReadUInt16BigEndian(attributes.Slice(offset + 2, 2));
            var valueStart = offset + 4;
            if (valueStart + attrLength > attributes.Length) break; // malformed, stop parsing rather than throw

            if (attrType == wantedType)
            {
                var endpoint = TryParseAddressValue(attributes.Slice(valueStart, attrLength), transactionId, xor);
                if (endpoint is not null) return endpoint;
            }

            // Attributes are padded to a 4-byte boundary.
            var padded = (attrLength + 3) & ~3;
            offset = valueStart + padded;
        }
        return null;
    }

    /// <summary>
    /// Decodes a MAPPED-ADDRESS-shaped attribute value. Internal rather than private because
    /// TURN's XOR-RELAYED-ADDRESS and XOR-PEER-ADDRESS use this exact encoding -- see
    /// RemoteControl.Net.Turn.TurnMessage, which reuses this rather than duplicating the XOR.
    /// </summary>
    internal static IPEndPoint? TryParseAddressValue(ReadOnlySpan<byte> value, ReadOnlySpan<byte> transactionId, bool xor)
    {
        if (value.Length < 4) return null;
        var family = value[1];
        var port = BinaryPrimitives.ReadUInt16BigEndian(value[2..4]);
        if (xor) port = (ushort)(port ^ (MagicCookie >> 16));

        if (family == 0x01) // IPv4
        {
            if (value.Length < 8) return null;
            Span<byte> addressBytes = stackalloc byte[4];
            value.Slice(4, 4).CopyTo(addressBytes);
            if (xor)
            {
                Span<byte> cookieBytes = stackalloc byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(cookieBytes, MagicCookie);
                for (var i = 0; i < 4; i++) addressBytes[i] ^= cookieBytes[i];
            }
            return new IPEndPoint(new IPAddress(addressBytes), port);
        }

        if (family == 0x02) // IPv6
        {
            if (value.Length < 20) return null;
            Span<byte> addressBytes = stackalloc byte[16];
            value.Slice(4, 16).CopyTo(addressBytes);
            if (xor)
            {
                Span<byte> xorKey = stackalloc byte[16];
                BinaryPrimitives.WriteUInt32BigEndian(xorKey[..4], MagicCookie);
                transactionId.CopyTo(xorKey[4..]);
                for (var i = 0; i < 16; i++) addressBytes[i] ^= xorKey[i];
            }
            return new IPEndPoint(new IPAddress(addressBytes), port);
        }

        return null;
    }
}
