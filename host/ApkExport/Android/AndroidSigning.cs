using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Android.Util;
using Android.Runtime;
using Java.Security;
using PublicKey = System.Security.Cryptography.X509Certificates.PublicKey;
using JCertificateFactory = Java.Security.Cert.CertificateFactory;

namespace GmIde;

/// <summary>
/// Keystore dos APKs gerados no aparelho (docs/apk-export.md, "Assinatura").
///
/// A IDE exige um keystore em Preferences > Platform Settings > Android > Keystore antes do
/// "Create Executable". Se o usuario nao tiver um, criamos um PKCS#12 em
/// Documents/GameMakerIDE/gmide-release.p12 (fora da pasta do app: sobrevive a reinstalar a IDE,
/// e o mesmo jogo precisa da mesma chave para ser atualizado). A senha fica ao lado, em
/// gmide-release.p12.json (uso pessoal; guarde os dois arquivos).
/// Chaves e assinatura ficam no java.security do Android (KeyStore PKCS12/BKS); o .NET so monta
/// o certificado X.509 (CertificateRequest) assinando com a chave Java.
/// </summary>
static class AndroidSigning
{
    const string Tag = "GmIde.Apk";
    public const string DefaultAlias = "gmide";

    public static string DefaultKeystore =>
        Path.Combine(global::Android.OS.Environment.GetExternalStoragePublicDirectory(global::Android.OS.Environment.DirectoryDocuments)!.AbsolutePath,
            "GameMakerIDE", "gmide-release.p12");

    static string SecretPath(string keystore) => keystore + ".json";

    sealed record Secret(string password, string alias);

    public static bool IsDefault(string? path) =>
        !string.IsNullOrEmpty(path) && Path.GetFullPath(path) == Path.GetFullPath(DefaultKeystore);

    /// <summary>Cria o keystore padrao se ainda nao existir. Devolve o caminho.</summary>
    public static string EnsureDefault()
    {
        var path = DefaultKeystore;
        if (File.Exists(path) && File.Exists(SecretPath(path))) return path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('/', 'x').Replace('+', 'y');

        var kpg = KeyPairGenerator.GetInstance("RSA")!;
        kpg.Initialize(2048);
        var kp = kpg.GenerateKeyPair()!;
        var spki = kp.Public!.GetEncoded()!;
        var pub = PublicKey.CreateFromSubjectPublicKeyInfo(spki, out _);
        var subject = new X500DistinguishedName("CN=GameMaker IDE Android, O=gmide");
        var req = new CertificateRequest(subject, pub, HashAlgorithmName.SHA256);
        var serial = RandomNumberGenerator.GetBytes(8);
        serial[0] &= 0x7F;
        byte[] der;
        using (var cert = req.Create(subject, new JavaRsaGenerator(kp.Private!, spki),
                   DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(30), serial))
            der = cert.RawData;

        var jcert = JCertificateFactory.GetInstance("X.509")!.GenerateCertificate(new MemoryStream(der))!;
        var ks = KeyStore.GetInstance("PKCS12")!;
        ks.Load(null, null);
        ks.SetKeyEntry(DefaultAlias, kp.Private, password.ToCharArray(), new[] { jcert });
        var tmp = path + ".tmp";
        using (var fs = File.Create(tmp)) ks.Store(fs, password.ToCharArray());
        File.WriteAllText(SecretPath(path), JsonSerializer.Serialize(new Secret(password, DefaultAlias)));
        File.Move(tmp, path, overwrite: true);
        Log.Info(Tag, $"Keystore criado: {path} (SHA-256 do certificado {Convert.ToHexString(SHA256.HashData(der))})");
        return path;
    }

    /// <summary>
    /// Carrega a chave para assinar. Para o keystore padrao a senha vem do .json ao lado; para o
    /// do usuario, das variaveis que o Igor define a partir das preferencias da IDE.
    /// </summary>
    public static IApkSigningKey Load(string path, string? storePassword, string? alias, string? keyPassword)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("keystore nao encontrado", path);
        if (IsDefault(path) && File.Exists(SecretPath(path)))
        {
            var s = JsonSerializer.Deserialize<Secret>(File.ReadAllText(SecretPath(path)))!;
            storePassword = keyPassword = s.password;
            alias = s.alias;
        }
        Exception? last = null;
        foreach (var type in new[] { "PKCS12", "BKS" })
        {
            bool loaded = false;
            try
            {
                var ks = KeyStore.GetInstance(type)!;
                using (var fs = File.OpenRead(path)) ks.Load(fs, (storePassword ?? "").ToCharArray());
                loaded = true;
                if (!string.IsNullOrEmpty(alias) && !ks.ContainsAlias(alias))
                    throw new InvalidDataException("O alias configurado nao existe no keystore: " + alias);
                var a = string.IsNullOrEmpty(alias) ? FirstKeyAlias(ks) : alias;
                if (a == null) throw new InvalidDataException("keystore sem chave privada");
                var storedKey = ks.GetKey(a, (string.IsNullOrEmpty(keyPassword) ? storePassword ?? "" : keyPassword).ToCharArray())
                          ?? throw new InvalidDataException($"alias {a} sem chave privada");
                // GetKey e declarado como IKey: o wrapper gerenciado nao e necessariamente
                // IPrivateKey. JavaCast consulta o tipo Java real, em vez de usar o cast C#.
                var key = storedKey.JavaCast<IPrivateKey>();
                var cert = ks.GetCertificate(a) ?? throw new InvalidDataException($"alias {a} sem certificado");
                if (key.Algorithm != "RSA") throw new NotSupportedException($"chave {key.Algorithm}: so RSA e suportada");
                return new JavaSigningKey(key, cert.GetEncoded()!, cert.PublicKey!.GetEncoded()!);
            }
            catch (Exception e)
            {
                Log.Warn(Tag, $"Leitura de keystore {type}: {e.GetType().Name}: {e.Message}");
                if (loaded) throw new InvalidDataException($"O keystore abriu como {type}, mas nao foi possivel usar sua chave. Confira alias e senha: {e.Message}", e);
                last = e;
            }
        }
        throw new InvalidDataException(
            $"nao consegui abrir o keystore {path} como PKCS12/BKS ({last?.Message}). " +
            "Keystores JKS antigos precisam ser convertidos no PC: keytool -importkeystore -deststoretype PKCS12", last);
    }

    static string? FirstKeyAlias(KeyStore ks)
    {
        var e = ks.Aliases();
        while (e != null && e.HasMoreElements)
        {
            var a = e.NextElement()?.ToString();
            if (a != null && ks.IsKeyEntry(a)) return a;
        }
        return null;
    }

    sealed class JavaSigningKey : IApkSigningKey
    {
        readonly IPrivateKey _key;
        public byte[] CertificateDer { get; }
        public byte[] SubjectPublicKeyInfo { get; }
        public JavaSigningKey(IPrivateKey key, byte[] cert, byte[] spki) { _key = key; CertificateDer = cert; SubjectPublicKeyInfo = spki; }
        public byte[] SignSha256Rsa(byte[] data) => JavaSign(_key, data);
    }

    static byte[] JavaSign(IPrivateKey key, byte[] data)
    {
        var s = Signature.GetInstance("SHA256withRSA")!;
        s.InitSign(key);
        s.Update(data);
        return s.Sign()!;
    }

    /// <summary>Assina o TBSCertificate com a chave privada do java.security.</summary>
    sealed class JavaRsaGenerator : X509SignatureGenerator
    {
        static readonly byte[] Sha256WithRsa = { 0x30, 0x0D, 0x06, 0x09, 0x2A, 0x86, 0x48, 0x86, 0xF7, 0x0D, 0x01, 0x01, 0x0B, 0x05, 0x00 };
        readonly IPrivateKey _key;
        readonly byte[] _spki;
        public JavaRsaGenerator(IPrivateKey key, byte[] spki) { _key = key; _spki = spki; }
        public override byte[] GetSignatureAlgorithmIdentifier(HashAlgorithmName h) =>
            h == HashAlgorithmName.SHA256 ? Sha256WithRsa : throw new NotSupportedException(h.Name);
        public override byte[] SignData(byte[] data, HashAlgorithmName h) => JavaSign(_key, data);
        protected override PublicKey BuildPublicKey() => PublicKey.CreateFromSubjectPublicKeyInfo(_spki, out _);
    }
}
