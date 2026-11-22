using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace T.Services;

/// <summary>An OpenSSH certificate as far as the client needs to check it before use.</summary>
internal sealed record OpenSshCertificate(
    byte[] PublicPoint,
    uint Type,
    string KeyId,
    IReadOnlyList<string> Principals,
    DateTimeOffset ValidAfter,
    DateTimeOffset ValidBefore);

/// <summary>
/// OpenSSH wire format for ECDSA P-256 public keys (RFC 5656) and their certificates
/// (OpenSSH PROTOCOL.certkeys). The parser treats its input as untrusted: every length is
/// bounds-checked and anything unexpected throws <see cref="FormatException"/>.
/// </summary>
internal static class OpenSshFormat
{
    public const string KeyType = "ecdsa-sha2-nistp256";
    public const string CertificateType = "ecdsa-sha2-nistp256-cert-v01@openssh.com";
    public const uint UserCertificate = 1;
    private const string Curve = "nistp256";
    private const int MaxCertificateBytes = 16 * 1024;

    /// <summary>Uncompressed EC point (0x04 || X || Y).</summary>
    public static byte[] EncodePoint(ECParameters parameters)
    {
        var x = parameters.Q.X!;
        var y = parameters.Q.Y!;
        var point = new byte[1 + x.Length + y.Length];
        point[0] = 0x04;
        x.CopyTo(point, 1);
        y.CopyTo(point, 1 + x.Length);
        return point;
    }

    /// <summary>"ecdsa-sha2-nistp256 AAAA... comment" (the format of an id_ecdsa.pub file).</summary>
    public static string FormatPublicKey(ECDsa key, string comment)
    {
        var point = EncodePoint(key.ExportParameters(includePrivateParameters: false));
        using var blob = new MemoryStream();
        WriteString(blob, Encoding.ASCII.GetBytes(KeyType));
        WriteString(blob, Encoding.ASCII.GetBytes(Curve));
        WriteString(blob, point);
        return $"{KeyType} {Convert.ToBase64String(blob.ToArray())} {comment}";
    }

    /// <summary>Parses an OpenSSH certificate line ("&lt;type&gt; &lt;base64&gt; [comment]").</summary>
    public static OpenSshCertificate ParseCertificate(string line)
    {
        var parts = line.Trim().Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts[0] != CertificateType)
            throw new FormatException("Not an ECDSA P-256 OpenSSH certificate.");
        if (parts[1].Length > (MaxCertificateBytes + 2) / 3 * 4)
            throw new FormatException("The certificate is too large.");

        var reader = new SshReader(Convert.FromBase64String(parts[1]));
        if (reader.ReadString() != CertificateType)
            throw new FormatException("The certificate type does not match its content.");

        reader.ReadBytes(); // nonce
        if (reader.ReadString() != Curve)
            throw new FormatException("Unexpected certificate curve.");

        var point = reader.ReadBytes().ToArray();
        reader.ReadUInt64(); // serial
        var type = reader.ReadUInt32();
        var keyId = reader.ReadString();

        var principals = new List<string>();
        var principalReader = new SshReader(reader.ReadBytes());
        while (!principalReader.IsAtEnd)
            principals.Add(principalReader.ReadString());

        var validAfter = reader.ReadUInt64();
        var validBefore = reader.ReadUInt64();

        reader.ReadBytes(); // critical options
        reader.ReadBytes(); // extensions
        reader.ReadBytes(); // reserved
        reader.ReadBytes(); // signature key
        reader.ReadBytes(); // signature
        if (!reader.IsAtEnd)
            throw new FormatException("Unexpected data after the certificate signature.");

        return new OpenSshCertificate(point, type, keyId, principals, ToTime(validAfter), ToTime(validBefore));
    }

    private static DateTimeOffset ToTime(ulong unixSeconds) =>
        unixSeconds >= (ulong)DateTimeOffset.MaxValue.ToUnixTimeSeconds()
            ? DateTimeOffset.MaxValue
            : DateTimeOffset.FromUnixTimeSeconds((long)unixSeconds);

    private static void WriteString(Stream stream, ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)value.Length);
        stream.Write(length);
        stream.Write(value);
    }

    private ref struct SshReader
    {
        private ReadOnlySpan<byte> _data;

        public SshReader(ReadOnlySpan<byte> data) => _data = data;

        public readonly bool IsAtEnd => _data.IsEmpty;

        public uint ReadUInt32()
        {
            if (_data.Length < 4) throw new FormatException("The certificate is truncated.");
            var value = BinaryPrimitives.ReadUInt32BigEndian(_data);
            _data = _data[4..];
            return value;
        }

        public ulong ReadUInt64()
        {
            if (_data.Length < 8) throw new FormatException("The certificate is truncated.");
            var value = BinaryPrimitives.ReadUInt64BigEndian(_data);
            _data = _data[8..];
            return value;
        }

        public ReadOnlySpan<byte> ReadBytes()
        {
            var length = ReadUInt32();
            if (length > (uint)_data.Length) throw new FormatException("The certificate is truncated.");
            var value = _data[..(int)length];
            _data = _data[(int)length..];
            return value;
        }

        public string ReadString() => Encoding.UTF8.GetString(ReadBytes());
    }
}
