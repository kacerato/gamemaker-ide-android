using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Android.Util;

namespace GmIde;

// Substituto da YoYoStudio.Utils.SecurityChecks.VerifyDLLs no Android.
//
// O original verifica a assinatura Authenticode (WinVerifyTrust) de 6 DLLs e
// desliga a IDE se falhar. WinVerifyTrust nao existe no Android, entao a
// checagem falharia sempre. Aqui a mesma garantia e feita em duas etapas:
//  1. No PC, o deploy.sh verifica com o Authenticode do Windows que essas DLLs
//     sao assinadas pela YOYO GAMES LIMITED e grava o SHA-256 de cada uma em
//     ide-integrity.json (junto da IDE na pasta de passagem).
//  2. Aqui conferimos esses hashes na copia do app. So se todos baterem a
//     lista do VerifyDLLs e esvaziada (as DLLs ja foram verificadas). Se algo
//     nao bater, a checagem original fica ativa e a IDE se desliga como no PC.
static class IntegrityCheck
{
    const string Tag = "GmIde";
    public const string ManifestName = "ide-integrity.json";

    public static void ReplaceVerifyDlls(string ideDir)
    {
        var secType = typeof(YoYoStudio.IDE).Assembly.GetType("YoYoStudio.Utils.SecurityChecks");
        var field = secType?.GetField("ms_files", BindingFlags.NonPublic | BindingFlags.Static);
        if (field?.GetValue(null) is not string[] files)
        {
            Log.Warn(Tag, "SecurityChecks.ms_files nao encontrado; VerifyDLLs fica como esta");
            return;
        }

        var manifest = Path.Combine(ideDir, ManifestName);
        if (!File.Exists(manifest))
        {
            Log.Error(Tag, $"{ManifestName} ausente: a verificacao de assinatura do PC nao foi feita. " +
                           "Rode o deploy.sh. A IDE vai se desligar no VerifyDLLs.");
            return;
        }

        Dictionary<string, string>? expected;
        try
        {
            expected = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(manifest));
        }
        catch (Exception e)
        {
            Log.Error(Tag, $"{ManifestName} invalido: {e.Message}");
            return;
        }

        foreach (var name in files)
        {
            var dll = name + ".dll";
            var path = Path.Combine(ideDir, dll);
            // Se o IdePatches alterou a DLL, ele guardou o original em "<dll>.original":
            // conferimos o original (o patch foi aplicado sobre essa copia autentica).
            if (File.Exists(path + ".original")) path += ".original";
            if (expected == null || !expected.TryGetValue(dll, out var want) || !File.Exists(path))
            {
                Log.Error(Tag, $"Integridade: {dll} sem hash verificado; VerifyDLLs original mantido");
                return;
            }
            var got = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            if (!got.Equals(want, StringComparison.OrdinalIgnoreCase))
            {
                Log.Error(Tag, $"Integridade: {dll} difere da copia assinada (esperado {want}, obtido {got}); " +
                               "VerifyDLLs original mantido");
                return;
            }
        }

        field.SetValue(null, Array.Empty<string>());
        Log.Info(Tag, $"Integridade: {files.Length} DLLs conferem com as assinadas no PC; VerifyDLLs dispensado");
    }
}
