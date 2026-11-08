using System.Security.Cryptography;
using System.Text;

namespace GmIde;

/// <summary>Chave de assinatura: certificado X.509 (DER), SubjectPublicKeyInfo (DER) e RSA PKCS#1 v1.5 com SHA-256.</summary>
interface IApkSigningKey
{
    byte[] CertificateDer { get; }
    byte[] SubjectPublicKeyInfo { get; }
    byte[] SignSha256Rsa(byte[] data);
}

/// <summary>Chave em memoria (System.Security.Cryptography): usada no PC e como fallback.</summary>
sealed class RsaSigningKey : IApkSigningKey
{
    readonly RSA _rsa;
    public byte[] CertificateDer { get; }
    public byte[] SubjectPublicKeyInfo { get; }
    public RsaSigningKey(RSA rsa, byte[] certDer) { _rsa = rsa; CertificateDer = certDer; SubjectPublicKeyInfo = rsa.ExportSubjectPublicKeyInfo(); }
    public byte[] SignSha256Rsa(byte[] data) => _rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

    /// <summary>Chave nova com certificado autoassinado (validade de 30 anos, como pede o Google Play: 25+).</summary>
    public static RsaSigningKey CreateSelfSigned(string commonName)
    {
        var rsa = RSA.Create(2048);
        var req = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=" + commonName, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var serial = RandomNumberGenerator.GetBytes(8);
        serial[0] &= 0x7F;
        var gen = System.Security.Cryptography.X509Certificates.X509SignatureGenerator.CreateForRSA(rsa, RSASignaturePadding.Pkcs1);
        using var cert = req.Create(req.SubjectName, gen, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(30), serial);
        return new RsaSigningKey(rsa, cert.RawData);
    }
}

/// <summary>
/// APK Signature Scheme v2 (https://source.android.com/docs/security/features/apksigning/v2):
/// bloco de assinatura inserido antes do diretorio central. Com minSdk 24 o v1 (JAR) nao e
/// necessario. Um signatario, algoritmo RSASSA-PKCS1-v1_5 + SHA2-256 (0x0103).
/// </summary>
static class ApkSignerV2
{
    const uint AlgRsaPkcs1Sha256 = 0x0103;
    const uint V2BlockId = 0x7109871a;
    const int Chunk = 1 << 20;

    public static void Sign(string unsignedApk, string outApk, IApkSigningKey key)
    {
        using var input = File.OpenRead(unsignedApk);
        long len = input.Length;
        int tail = (int)Math.Min(len, 22 + 0xFFFF);
        var buf = new byte[tail];
        input.Position = len - tail;
        input.ReadExactly(buf);
        int eocdRel = -1;
        for (int i = tail - 22; i >= 0; i--)
            if (BitConverter.ToUInt32(buf, i) == 0x06054b50) { eocdRel = i; break; }
        if (eocdRel < 0) throw new InvalidDataException("APK sem fim do diretorio central");
        long eocd = len - tail + eocdRel;
        long cdOffset = BitConverter.ToUInt32(buf, eocdRel + 16);
        long cdSize = BitConverter.ToUInt32(buf, eocdRel + 12);
        if (cdOffset + cdSize != eocd) throw new InvalidDataException("APK com dados entre o diretorio central e o fim (comentario/zip64?)");
        var eocdBytes = new byte[len - eocd];
        Array.Copy(buf, eocdRel, eocdBytes, 0, eocdBytes.Length);

        // Digest em pedacos de 1 MiB das tres secoes (conteudo, diretorio central, EOCD).
        var chunkDigests = new MemoryStream();
        int chunks = 0;
        void DigestRange(Stream s, long start, long count)
        {
            var b = new byte[Chunk];
            s.Position = start;
            while (count > 0)
            {
                int n = (int)Math.Min(Chunk, count);
                s.ReadExactly(b, 0, n);
                using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var pre = new byte[5];
                pre[0] = 0xa5;
                BitConverter.TryWriteBytes(pre.AsSpan(1), (uint)n);
                h.AppendData(pre);
                h.AppendData(b, 0, n);
                chunkDigests.Write(h.GetHashAndReset());
                chunks++;
                count -= n;
            }
        }
        DigestRange(input, 0, cdOffset);
        DigestRange(input, cdOffset, cdSize);
        DigestRange(new MemoryStream(eocdBytes), 0, eocdBytes.Length);
        byte[] digest;
        using (var top = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            var pre = new byte[5];
            pre[0] = 0x5a;
            BitConverter.TryWriteBytes(pre.AsSpan(1), (uint)chunks);
            top.AppendData(pre);
            top.AppendData(chunkDigests.ToArray());
            digest = top.GetHashAndReset();
        }

        byte[] signedData = Concat(
            Lp(Lp(Concat(U32(AlgRsaPkcs1Sha256), Lp(digest)))),   // digests
            Lp(Lp(key.CertificateDer)),                            // certificados
            Lp(Array.Empty<byte>()));                              // atributos adicionais
        byte[] signature = key.SignSha256Rsa(signedData);
        byte[] signer = Concat(
            Lp(signedData),
            Lp(Lp(Concat(U32(AlgRsaPkcs1Sha256), Lp(signature)))),
            Lp(key.SubjectPublicKeyInfo));
        byte[] v2Value = Lp(Lp(signer));

        byte[] pair = Concat(U64((ulong)(4 + v2Value.Length)), U32(V2BlockId), v2Value);
        ulong blockSize = (ulong)(pair.Length + 8 + 16);
        byte[] block = Concat(U64(blockSize), pair, U64(blockSize), Encoding.ASCII.GetBytes("APK Sig Block 42"));

        // EOCD com o offset do diretorio central deslocado pelo bloco.
        BitConverter.GetBytes((uint)(cdOffset + block.Length)).CopyTo(eocdBytes, 16);
        using var output = new FileStream(outApk, FileMode.Create, FileAccess.Write);
        CopyRange(input, output, 0, cdOffset);
        output.Write(block);
        CopyRange(input, output, cdOffset, cdSize);
        output.Write(eocdBytes);
    }

    static void CopyRange(Stream from, Stream to, long start, long count)
    {
        var b = new byte[1 << 16];
        from.Position = start;
        while (count > 0)
        {
            int n = from.Read(b, 0, (int)Math.Min(b.Length, count));
            if (n <= 0) throw new EndOfStreamException();
            to.Write(b, 0, n);
            count -= n;
        }
    }

    static byte[] U32(uint v) => BitConverter.GetBytes(v);
    static byte[] U64(ulong v) => BitConverter.GetBytes(v);
    static byte[] Lp(byte[] v) => Concat(U32((uint)v.Length), v);
    static byte[] Concat(params byte[][] parts)
    {
        var r = new byte[parts.Sum(p => p.Length)];
        int o = 0;
        foreach (var p in parts) { Buffer.BlockCopy(p, 0, r, o, p.Length); o += p.Length; }
        return r;
    }
}
