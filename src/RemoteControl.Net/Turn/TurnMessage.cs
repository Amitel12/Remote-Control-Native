using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using RemoteControl.Net.Stun;

namespace RemoteControl.Net.Turn;

/// <summary>TURN methods used here (RFC 5766). ChannelBind is deliberately absent -- see <see cref="TurnClient"/>.</summary>
public enum TurnMethod : ushort
{
    Allocate = 0x003,
    Refresh = 0x004,
    Send = 0x006,
    Data = 0x007,
    CreatePermission = 0x008,
}

/// <summary>The four STUN message classes (RFC 5389 section 6).</summary>
public enum StunClass
{
    Request = 0,
    Indication = 1,
    SuccessResponse = 2,
    ErrorResponse = 3,
}

/// <summary>What <see cref="TurnMessage.TryParse"/> could read out of a datagram.</summary>
public sealed record TurnParsedMessage(
    TurnMethod Method,
    StunClass Class,
    byte[] TransactionId,
    IPEndPoint? RelayedEndpoint,
    IPEndPoint? MappedEndpoint,
    IPEndPoint? PeerEndpoint,
    byte[]? Data,
    uint? LifetimeSeconds,
    int? ErrorCode,
    string? Realm,
    string? Nonce);

/// <summary>
/// TURN's wire format (RFC 5766), which is STUN's (RFC 5389) with more
/// methods and attributes -- so this builds on <see cref="StunMessage"/>'s
/// proven header/attribute handling rather than reimplementing it, and
/// stays in the same spirit: only the message shapes this app actually
/// sends and receives, not a general TURN library.
///
/// Written against the coturn deployment this app already has
/// (`deploy/turnserver.conf.example` in the amitel12/tests repo):
/// `lt-cred-mech` long-term credentials, one static user, plain UDP on
/// 3478, no TLS. That is why only the long-term credential mechanism is
/// implemented here -- short-term credentials never appear on this path.
/// </summary>
public static class TurnMessage
{
    public const ushort UsernameAttribute = 0x0006;
    public const ushort MessageIntegrityAttribute = 0x0008;
    public const ushort ErrorCodeAttribute = 0x0009;
    public const ushort LifetimeAttribute = 0x000D;
    public const ushort XorPeerAddressAttribute = 0x0012;
    public const ushort DataAttribute = 0x0013;
    public const ushort RealmAttribute = 0x0014;
    public const ushort NonceAttribute = 0x0015;
    public const ushort XorRelayedAddressAttribute = 0x0016;
    public const ushort RequestedTransportAttribute = 0x0019;
    public const ushort XorMappedAddressAttribute = 0x0020;
    public const ushort FingerprintAttribute = 0x8028;

    private const int HeaderLength = 20;
    private const uint MagicCookie = 0x2112A442;
    private const uint FingerprintXor = 0x5354554E;
    private const byte UdpTransportProtocol = 17;

    public static byte[] NewTransactionId()
    {
        // Cryptographically random, per RFC 5389 section 6 -- the transaction ID is what stops
        // an off-path attacker forging a response, so Random.Shared is not good enough here.
        var transactionId = new byte[StunMessage.TransactionIdLength];
        RandomNumberGenerator.Fill(transactionId);
        return transactionId;
    }

    /// <summary>
    /// The long-term credential key: MD5(username ":" realm ":" password), per RFC 5389
    /// section 15.4. MD5 is not a choice here -- the protocol specifies it, and coturn will
    /// reject anything else -- so this is one of the rare correct uses of it.
    /// </summary>
    public static byte[] LongTermKey(string username, string realm, string password) =>
        MD5.HashData(Encoding.UTF8.GetBytes($"{username}:{realm}:{password}"));

    /// <summary>
    /// Assembles one message. When <paramref name="integrityKey"/> is supplied a
    /// MESSAGE-INTEGRITY attribute is appended (HMAC-SHA1 over the message with the header
    /// length already counting that attribute -- the classic place to get this wrong), then a
    /// FINGERPRINT, which coturn is configured to use.
    /// </summary>
    public static byte[] Build(
        TurnMethod method,
        StunClass messageClass,
        ReadOnlySpan<byte> transactionId,
        IReadOnlyList<byte[]> attributes,
        byte[]? integrityKey = null)
    {
        var body = new List<byte>();
        foreach (var attribute in attributes) body.AddRange(attribute);

        // MESSAGE-INTEGRITY is computed over the message as if it were already present: header
        // length includes its 24 bytes (4 header + 20 value), but the bytes hashed stop before
        // it. Same again for FINGERPRINT's 8 bytes afterwards.
        var message = new List<byte>(HeaderLength + body.Count + 32);
        message.AddRange(new byte[HeaderLength]);
        message.AddRange(body);

        WriteHeader(message, method, messageClass, transactionId, contentLength: body.Count);

        if (integrityKey is not null)
        {
            WriteHeaderLength(message, body.Count + 24);
            var integrity = HMACSHA1.HashData(integrityKey, message.ToArray());
            message.AddRange(BuildAttribute(MessageIntegrityAttribute, integrity));
        }

        WriteHeaderLength(message, message.Count - HeaderLength + 8);
        var crc = Crc32.Compute(message.ToArray()) ^ FingerprintXor;
        var fingerprintValue = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(fingerprintValue, crc);
        message.AddRange(BuildAttribute(FingerprintAttribute, fingerprintValue));

        WriteHeaderLength(message, message.Count - HeaderLength);
        return message.ToArray();
    }

    public static byte[] BuildAttribute(ushort type, ReadOnlySpan<byte> value)
    {
        var padded = (value.Length + 3) & ~3;
        var attribute = new byte[4 + padded];
        BinaryPrimitives.WriteUInt16BigEndian(attribute.AsSpan(0, 2), type);
        BinaryPrimitives.WriteUInt16BigEndian(attribute.AsSpan(2, 2), (ushort)value.Length);
        value.CopyTo(attribute.AsSpan(4));
        return attribute;
    }

    public static byte[] BuildStringAttribute(ushort type, string value) =>
        BuildAttribute(type, Encoding.UTF8.GetBytes(value));

    public static byte[] BuildLifetime(uint seconds)
    {
        var value = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(value, seconds);
        return BuildAttribute(LifetimeAttribute, value);
    }

    /// <summary>REQUESTED-TRANSPORT is a protocol number in the top byte; 17 is UDP, the only one coturn relays here.</summary>
    public static byte[] BuildRequestedTransportUdp() =>
        BuildAttribute(RequestedTransportAttribute, [UdpTransportProtocol, 0, 0, 0]);

    /// <summary>Encodes an XOR-*-ADDRESS attribute (IPv4 only, matching the rest of this app's transport).</summary>
    public static byte[] BuildXorAddress(ushort type, IPEndPoint endpoint)
    {
        var address = endpoint.Address.GetAddressBytes();
        if (address.Length != 4)
            throw new ArgumentException("Only IPv4 peer/relay addresses are supported.", nameof(endpoint));

        var value = new byte[8];
        value[0] = 0;
        value[1] = 0x01; // family: IPv4
        BinaryPrimitives.WriteUInt16BigEndian(value.AsSpan(2, 2), (ushort)(endpoint.Port ^ (MagicCookie >> 16)));

        Span<byte> cookie = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(cookie, MagicCookie);
        for (var i = 0; i < 4; i++) value[4 + i] = (byte)(address[i] ^ cookie[i]);
        return BuildAttribute(type, value);
    }

    /// <summary>
    /// Reads whatever of interest a datagram contains. Returns false for anything that isn't a
    /// well-formed STUN/TURN message -- a raw UDP socket sees stray and stale traffic, and this
    /// runs on the same socket as the media stream, so "not ours" has to be cheap and silent.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> datagram, out TurnParsedMessage message)
    {
        message = null!;
        if (datagram.Length < HeaderLength) return false;

        var type = BinaryPrimitives.ReadUInt16BigEndian(datagram[0..2]);
        if ((type & 0xC000) != 0) return false; // top two bits must be zero for STUN.

        var attributesLength = BinaryPrimitives.ReadUInt16BigEndian(datagram[2..4]);
        if (BinaryPrimitives.ReadUInt32BigEndian(datagram[4..8]) != MagicCookie) return false;
        if (HeaderLength + attributesLength > datagram.Length) return false;

        var transactionId = datagram.Slice(8, StunMessage.TransactionIdLength);
        var method = (ushort)(((type & 0x3E00) >> 2) | ((type & 0x00E0) >> 1) | (type & 0x000F));
        var messageClass = (StunClass)(((type & 0x0100) >> 7) | ((type & 0x0010) >> 4));

        IPEndPoint? relayed = null, mapped = null, peer = null;
        byte[]? data = null;
        uint? lifetime = null;
        int? errorCode = null;
        string? realm = null, nonce = null;

        var attributes = datagram.Slice(HeaderLength, attributesLength);
        var offset = 0;
        while (offset + 4 <= attributes.Length)
        {
            var attrType = BinaryPrimitives.ReadUInt16BigEndian(attributes.Slice(offset, 2));
            var attrLength = BinaryPrimitives.ReadUInt16BigEndian(attributes.Slice(offset + 2, 2));
            var valueStart = offset + 4;
            if (valueStart + attrLength > attributes.Length) break; // malformed: keep what we have.

            var value = attributes.Slice(valueStart, attrLength);
            switch (attrType)
            {
                case XorRelayedAddressAttribute:
                    relayed = StunMessage.TryParseAddressValue(value, transactionId, xor: true);
                    break;
                case XorMappedAddressAttribute:
                    mapped = StunMessage.TryParseAddressValue(value, transactionId, xor: true);
                    break;
                case XorPeerAddressAttribute:
                    peer = StunMessage.TryParseAddressValue(value, transactionId, xor: true);
                    break;
                case DataAttribute:
                    data = value.ToArray();
                    break;
                case LifetimeAttribute:
                    if (attrLength >= 4) lifetime = BinaryPrimitives.ReadUInt32BigEndian(value[..4]);
                    break;
                case ErrorCodeAttribute:
                    // Class in the low 3 bits of byte 2, number (0-99) in byte 3 -- so 401 is
                    // class 4, number 1. Not a plain integer, a common misread.
                    if (attrLength >= 4) errorCode = ((value[2] & 0x07) * 100) + value[3];
                    break;
                case RealmAttribute:
                    realm = Encoding.UTF8.GetString(value);
                    break;
                case NonceAttribute:
                    nonce = Encoding.UTF8.GetString(value);
                    break;
            }

            offset = valueStart + ((attrLength + 3) & ~3); // attributes are 4-byte aligned.
        }

        message = new TurnParsedMessage(
            (TurnMethod)method, messageClass, transactionId.ToArray(),
            relayed, mapped, peer, data, lifetime, errorCode, realm, nonce);
        return true;
    }

    private static void WriteHeader(List<byte> message, TurnMethod method, StunClass messageClass, ReadOnlySpan<byte> transactionId, int contentLength)
    {
        var methodBits = (ushort)method;
        var classBits = (ushort)messageClass;
        var type = (ushort)(((methodBits & 0xF80) << 2) | ((methodBits & 0x70) << 1) | (methodBits & 0x0F)
                            | ((classBits & 0x02) << 7) | ((classBits & 0x01) << 4));

        var header = new byte[HeaderLength];
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(0, 2), type);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2, 2), (ushort)contentLength);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), MagicCookie);
        transactionId.CopyTo(header.AsSpan(8, StunMessage.TransactionIdLength));
        for (var i = 0; i < HeaderLength; i++) message[i] = header[i];
    }

    private static void WriteHeaderLength(List<byte> message, int length)
    {
        Span<byte> encoded = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(encoded, (ushort)length);
        message[2] = encoded[0];
        message[3] = encoded[1];
    }
}
