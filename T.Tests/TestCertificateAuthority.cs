using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace T.Tests;

/// <summary>Issues OpenSSH ECDSA user/host certificates, signed like step-ca does.</summary>
internal sealed class TestCertificateAuthority : IDisposable
{
    private readonly ECDsa _caKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public string Issue(
        string publicKeyLine,
        DateTimeOffset validAfter,
        DateTimeOffset validBefore,
        uint type = 1,
        string keyId = "alice@example.com",
        params string[] principals)
    {
        var point = ReadPublicPoint(publicKeyLine);

        using var body = new MemoryStream();
        WriteString(body, "ecdsa-sha2-nistp256-cert-v01@openssh.com");
        WriteBytes(body, RandomNumberGenerator.GetBytes(32));
        WriteString(body, "nistp256");
        WriteBytes(body, point);
        WriteUInt64(body, 42);
        WriteUInt32(body, type);
        WriteString(body, keyId);

        using (var list = new MemoryStream())
        {
            foreach (var principal in principals.Length > 0 ? principals : ["alice"])
                WriteString(list, principal);
            WriteBytes(body, list.ToArray());
        }

        WriteUInt64(body, (ulong)validAfter.ToUnixTimeSeconds());
        WriteUInt64(body, (ulong)validBefore.ToUnixTimeSeconds());
        WriteBytes(body, []); // critical options

        using (var extensions = new MemoryStream())
        {
            WriteString(extensions, "permit-pty");
            WriteBytes(extensions, []);
            WriteBytes(body, extensions.ToArray());
        }

        WriteBytes(body, []); // reserved

        using (var caPublicKey = new MemoryStream())
        {
            WriteString(caPublicKey, "ecdsa-sha2-nistp256");
            WriteString(caPublicKey, "nistp256");
            WriteBytes(caPublicKey, EncodePoint(_caKey));
            WriteBytes(body, caPublicKey.ToArray());
        }

        var signature = _caKey.SignData(body.ToArray(), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        using (var signatureBlob = new MemoryStream())
        using (var rs = new MemoryStream())
        {
            WriteMpint(rs, signature.AsSpan(0, 32));
            WriteMpint(rs, signature.AsSpan(32, 32));
            WriteString(signatureBlob, "ecdsa-sha2-nistp256");
            WriteBytes(signatureBlob, rs.ToArray());
            WriteBytes(body, signatureBlob.ToArray());
        }

        return "ecdsa-sha2-nistp256-cert-v01@openssh.com " + Convert.ToBase64String(body.ToArray()) + " test";
    }

    public static string NewPublicKeyLine()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var blob = new MemoryStream();
        WriteString(blob, "ecdsa-sha2-nistp256");
        WriteString(blob, "nistp256");
        WriteBytes(blob, EncodePoint(key));
        return "ecdsa-sha2-nistp256 " + Convert.ToBase64String(blob.ToArray());
    }

    public void Dispose() => _caKey.Dispose();

    private static byte[] ReadPublicPoint(string publicKeyLine)
    {
        var blob = Convert.FromBase64String(publicKeyLine.Split(' ')[1]);
        var offset = 0;
        byte[] Next()
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(offset));
            var value = blob.AsSpan(offset + 4, length).ToArray();
            offset += 4 + length;
            return value;
        }
        Next(); // key type
        Next(); // curve
        return Next();
    }

    private static byte[] EncodePoint(ECDsa key)
    {
        var q = key.ExportParameters(false).Q;
        return [0x04, .. q.X!, .. q.Y!];
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteUInt64(Stream stream, ulong value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteBytes(Stream stream, ReadOnlySpan<byte> value)
    {
        WriteUInt32(stream, (uint)value.Length);
        stream.Write(value);
    }

    private static void WriteString(Stream stream, string value) => WriteBytes(stream, Encoding.UTF8.GetBytes(value));

    private static void WriteMpint(Stream stream, ReadOnlySpan<byte> unsignedBigEndian)
    {
        var value = unsignedBigEndian.TrimStart((byte)0);
        if (value.Length > 0 && (value[0] & 0x80) != 0)
            WriteBytes(stream, [0, .. value]);
        else
            WriteBytes(stream, value);
    }
}
