using System.Text.Json.Nodes;

namespace GmIde;

// WriteAndroidPreferences grava o objeto tipado da IDE, com as senhas ja decifradas pela
// camada de Preferencias. O processo local nao precisa da licenca/email para le-las de novo.
internal static class ApkKeystoreCredentials
{
    internal static string ReadPassword(string buildFile, string selectedKey, string defaultKey, bool aliasPassword)
    {
        // A chave local tem senha propria no seu .p12.json, lida por AndroidSigning.Load.
        if (ApkKeystoreSelection.SamePath(selectedKey, defaultKey)) return "";
        var build = JsonNode.Parse(File.ReadAllText(buildFile)) ?? throw new InvalidDataException("Configuracao do build invalida.");
        string preferences = build["preferences"]?.GetValue<string>() ?? throw new InvalidDataException("Snapshot das Preferencias ausente.");
        var keystore = JsonNode.Parse(File.ReadAllText(preferences))?["keystore"] as JsonObject
            ?? throw new InvalidDataException("Credenciais do keystore ausentes nas Preferencias. Configure a chave e a senha na IDE.");
        string configuredKey = keystore["filename"]?.GetValue<string>() ?? "";
        if (string.IsNullOrWhiteSpace(configuredKey) || !ApkKeystoreSelection.SamePath(configuredKey, selectedKey))
            throw new InvalidOperationException("As credenciais das Preferencias nao correspondem ao keystore selecionado. Reabra a exportacao apos conferir a chave.");
        string password = keystore["password"]?.GetValue<string>() ?? "";
        string alias = keystore["alias_password"]?.GetValue<string>() ?? "";
        return aliasPassword && alias.Length > 0 ? alias : password;
    }
}
