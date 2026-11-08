using System.Text.Json.Nodes;

namespace GmIde;

// Authoring settings live with the project. Output paths remain private IDE preferences.
internal static class ProjectExportSettings
{
    internal static string DefaultId(string projectFile, string projectName)
    {
        string name = new string(projectName.ToLowerInvariant().Where(c => char.IsAsciiLetterOrDigit(c) || c == '_').Take(96).ToArray());
        if (name.Length == 0 || !char.IsAsciiLetter(name[0])) name = "game_" + name;
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(projectFile))))[..8].ToLowerInvariant();
        return "com.gmide." + name + "_" + hash;
    }

    static string Folder(string projectFile) => Path.Combine(Path.GetDirectoryName(projectFile)!, "options", "gmide-export");

    internal static JsonObject Read(string projectFile, string target)
    {
        string path = Path.Combine(Folder(projectFile), "settings.json");
        if (!File.Exists(path)) return new JsonObject();
        var result = JsonNode.Parse(File.ReadAllText(path))?[target]?.DeepClone()?.AsObject() ?? new JsonObject();
        if (result["icon"]?.GetValue<string>() is { } icon && icon == target + "-icon.png")
            result["icon_path"] = Path.Combine(Folder(projectFile), icon);
        return result;
    }

    internal static void Save(string projectFile, string target, JsonObject request, byte[]? iconPng)
    {
        if (target is not ("web" or "windows")) throw new ArgumentException("Destino invalido.");
        string folder = Folder(projectFile); Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "settings.json");
        var all = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsObject() : new JsonObject();
        var values = new JsonObject();
        foreach (string key in new[] { "display_name", "package", "version", "window_width", "window_height" })
            if (request[key] != null) values[key] = request[key]!.DeepClone();
        if (iconPng != null)
        {
            string icon = target + "-icon.png";
            File.WriteAllBytes(Path.Combine(folder, icon + ".tmp"), iconPng);
            File.Move(Path.Combine(folder, icon + ".tmp"), Path.Combine(folder, icon), true);
            values["icon"] = icon;
            if (target == "windows") File.WriteAllBytes(Path.Combine(folder, "windows-icon.ico"), WindowsWebPackage.IconFile(iconPng));
        }
        all[target] = values;
        File.WriteAllText(path + ".tmp", all.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
    }
}
