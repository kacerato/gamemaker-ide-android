using System.Text.Json;
using System.Text.Json.Nodes;

namespace GmIde;

// Mesma precedencia do Igor: local_settings.json sobre platform_setting_defaults.json.
internal static class ApkKeystoreSelection
{
    const string Preference = "machine.Platform Settings.Android.Keystore.filename";

    // A IDE grava estes JSON com virgula antes de '}' / ']' (e aceita comentarios ao ler).
    static readonly JsonDocumentOptions IdeJson = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    internal static string? ReadConfiguredPath(string buildFile)
    {
        var env = JsonNode.Parse(File.ReadAllText(buildFile)) ?? throw new InvalidDataException("Configuracao do build invalida.");
        string userDir = env["userDir"]?.GetValue<string>() ?? throw new InvalidDataException("Diretorio das preferencias ausente no build.");
        string runtime = env["runtimeLocation"]?.GetValue<string>() ?? throw new InvalidDataException("Runtime ausente no build.");
        foreach (string file in new[] { Path.Combine(userDir, "local_settings.json"), Path.Combine(runtime, "bin", "platform_setting_defaults.json") })
        {
            if (!File.Exists(file)) continue;
            JsonObject settings;
            try { settings = JsonNode.Parse(File.ReadAllText(file), documentOptions: IdeJson) as JsonObject ?? throw new InvalidDataException("conteudo vazio"); }
            catch (Exception e) when (e is JsonException or InvalidDataException)
            { throw new InvalidDataException($"Nao foi possivel ler {Path.GetFileName(file)}: {e.Message}", e); }
            if (settings.TryGetPropertyValue(Preference, out var value))
                return value?.GetValue<string>()?.Trim(); // vazio explicito tambem sobrepoe o default
        }
        return null;
    }

    internal static string Resolve(string? configured, Func<string> ensureDefault)
    {
        if (string.IsNullOrWhiteSpace(configured)) return ensureDefault();
        if (!File.Exists(configured))
            throw new FileNotFoundException("O keystore configurado nas Preferencias nao foi encontrado. Corrija o caminho para manter a assinatura do jogo.", configured);
        return configured;
    }

    internal static bool SamePath(string first, string second) =>
        string.Equals(Path.GetFullPath(first.Replace('\\', '/')), Path.GetFullPath(second.Replace('\\', '/')),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
