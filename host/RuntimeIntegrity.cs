using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace GmIde;

// Substituto da verificacao Authenticode das ferramentas de build (WinTrust.VerifySecurity
// no Build.dll), no mesmo modelo de IntegrityCheck (IDE):
//  1. No PC, tools/make-runtime-manifest.ps1 (chamado pelo deploy.sh) verifica com o
//     Authenticode do Windows, com as mesmas regras do original (assinatura valida,
//     thumbprint na lista do Build.dll, O=YoYo), Igor.exe, GMAssetCompiler.dll e
//     GMWebServer.dll do runtime, e grava o SHA-256 de cada um em
//     <runtime>/gmide-runtime-integrity.json.
//  2. Aqui conferimos tamanho + SHA-256 do arquivo no aparelho contra o manifesto.
// Sem manifesto, ou com qualquer divergencia, a verificacao falha (a IDE mostra
// "Corrupt or damaged GameMaker build tools." como no PC).
//
// Sem dependencias Android: o mesmo arquivo roda no PC (tools/build-test).
public static class RuntimeIntegrity
{
    public const string ManifestName = "gmide-runtime-integrity.json";
    public const string IgorRel = "bin/igor/windows/x64/Igor.exe";
    public const string AssetCompilerRel = "bin/assetcompiler/windows/x64/GMAssetCompiler.dll";
    public const string WebServerRel = "bin/webserver/windows/x64/GMWebServer.dll";

    /// <summary>Teste no PC: manifesto fora da pasta do runtime (que e do usuario e nao deve ser alterada).</summary>
    public static string? ManifestOverride;

    // Hash por (caminho, tamanho, mtime): o Igor.exe tem 37 MB e a IDE verifica a cada build.
    static readonly ConcurrentDictionary<string, (long Len, DateTime Mtime, string Hash)> _hashes = new();

    public static string ManifestPath(string runtimeDir) =>
        ManifestOverride ?? Path.Combine(runtimeDir, ManifestName);

    /// <summary>
    /// Confere <paramref name="rel"/> (caminho relativo ao runtime, com '/') contra o manifesto.
    /// <paramref name="orgs"/>: se dado, a organizacao assinante gravada no PC precisa ser uma delas
    /// (mesma comparacao ordinal do WinTrust.VerifySecurity).
    /// </summary>
    public static bool VerifyFile(string runtimeDir, string rel, string[]? orgs, out string error)
    {
        error = "";
        if (string.IsNullOrEmpty(runtimeDir)) { error = "runtime desconhecido"; return false; }
        var manifest = ManifestPath(runtimeDir);
        if (!File.Exists(manifest))
        {
            error = $"{manifest} ausente: a verificacao de assinatura no PC nao foi feita (rode deploy.sh)";
            return false;
        }
        JsonElement entry;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
            if (!doc.RootElement.TryGetProperty("files", out var files) || !files.TryGetProperty(rel, out entry))
            {
                error = $"{rel} fora do manifesto";
                return false;
            }
            entry = entry.Clone();
        }
        catch (Exception e)
        {
            error = $"{ManifestName} invalido: {e.Message}";
            return false;
        }

        var want = entry.TryGetProperty("sha256", out var h) ? h.GetString() ?? "" : "";
        var org = entry.TryGetProperty("org", out var o) ? o.GetString() ?? "" : "";
        long size = entry.TryGetProperty("size", out var s) && s.TryGetInt64(out var sz) ? sz : -1;
        if (want.Length != 64) { error = $"{rel}: hash ausente no manifesto"; return false; }
        if (orgs != null && Array.IndexOf(orgs, org) < 0)
        {
            error = $"{rel}: assinante '{org}' nao esta em [{string.Join(", ", orgs)}]";
            return false;
        }

        var path = Path.Combine(runtimeDir, rel);
        var fi = new FileInfo(path);
        if (!fi.Exists) { error = $"{path} nao existe"; return false; }
        if (size >= 0 && fi.Length != size) { error = $"{rel}: tamanho {fi.Length}, esperado {size}"; return false; }
        var got = Hash(fi);
        if (!got.Equals(want, StringComparison.OrdinalIgnoreCase))
        {
            error = $"{rel} difere da copia assinada (esperado {want}, obtido {got})";
            return false;
        }
        return true;
    }

    public static string Hash(FileInfo fi)
    {
        fi.Refresh();
        if (_hashes.TryGetValue(fi.FullName, out var c) && c.Len == fi.Length && c.Mtime == fi.LastWriteTimeUtc)
            return c.Hash;
        string hash;
        using (var fs = fi.OpenRead()) hash = Convert.ToHexString(SHA256.HashData(fs));
        _hashes[fi.FullName] = (fi.Length, fi.LastWriteTimeUtc, hash);
        return hash;
    }
}
