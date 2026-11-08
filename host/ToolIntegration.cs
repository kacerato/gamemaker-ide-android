using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Android.Content;
using Android.Util;

namespace GmIde;

// Liga o ProjectTool e o Language Server de GML da IDE ao stub libgmexec.so + processo :build
// (docs/external-tools.md). Chamado por BuildIntegration.Configure (lado da IDE) e por
// BuildService.OnCreate (lado do servico). Nada aqui altera DLL da IDE:
//
//  1. ProjectTool: ProjectToolLocator.FindTool() tenta, nesta ordem, a preferencia
//     "machine.Plugins.ProjectToolPrefs.Override", o pacote "@gm-tools/project-tool-<RID>"
//     (PackageToolLocator.FindToolPath: Path.Combine(<pasta do package.json>, gm.tool.path)) e
//     "./ProjectTool/ProjectTool". No Android o RID e o do runtime (android-arm64), que nao tem
//     pacote. Registramos um pacote "embutido" em ide/packages/gmide-project-tool-<RID>/ (a IDE
//     varre ./packages/*/*package.json, PackageMan.LoadBundledPackages) com gm.tool.path =
//     caminho ABSOLUTO do stub; Path.Combine devolve o absoluto e Path.Exists confere.
//     (A preferencia Override exigiria o nome terminar em "projecttool", e o stub e libgmexec.so.)
//  2. Language Server: ide/CodeEditor2/Servers/GML.lspConfig passa a ter "path" = stub e
//     "args" = "--gmexec-tool=GameMakerLanguageServer " + args originais. O original fica em
//     GML.lspConfig.original (refeito a cada boot a partir dele).
//  3. files/tool-bridge.json diz ao :build onde estao a IDE e o cache.
public static class ToolIntegration
{
    const string Tag = "GmIde.Tools";
    const string ConfigName = "tool-bridge.json";

    public sealed record ServiceConfig(string IdeDir, string CacheDir);

    /// <summary>Lado da IDE (processo principal), dentro de BuildIntegration.Configure.</summary>
    public static void Configure(string ideDir, string stubPath)
    {
        try
        {
            var app = MainApplication.Instance;
            var cfg = new ServiceConfig(Path.GetFullPath(ideDir), Path.Combine(app.NoBackupFilesDir!.AbsolutePath, "build-tools"));
            File.WriteAllText(Path.Combine(app.FilesDir!.AbsolutePath, ConfigName), JsonSerializer.Serialize(cfg));
            RegisterProjectToolPackage(ideDir, stubPath, RuntimeInformation.RuntimeIdentifier);
            RewriteLspConfig(ideDir, stubPath);
            if (!File.Exists(Path.Combine(ideDir, ToolBridge.ManifestName)))
                Log.Warn(Tag, $"{ToolBridge.ManifestName} ausente em {ideDir}: ProjectTool e Language Server vao ser recusados " +
                              "(tools/make-tools-manifest.ps1 no deploy)");
        }
        catch (Exception e)
        {
            Log.Error(Tag, "ToolIntegration.Configure: " + e);
        }
    }

    /// <summary>Lado do servico :build (BuildService.OnCreate).</summary>
    public static void ConfigureService(Context ctx)
    {
        try
        {
            var path = Path.Combine(ctx.FilesDir!.AbsolutePath, ConfigName);
            if (!File.Exists(path)) { Log.Warn(Tag, "tool-bridge.json ausente: ferramentas externas desligadas"); return; }
            var cfg = JsonSerializer.Deserialize<ServiceConfig>(File.ReadAllText(path))!;
            ToolBridge.Configure(cfg.IdeDir, cfg.CacheDir);
            ToolBridge.Logger = s => Log.Info(Tag, s);
            // Mesmo mapeamento de nativos da IDE (yoga.dll -> libyoga.so...) para as DLLs que o
            // Language Server carrega de ide/ (Utils, OSCore...). So afeta assemblies dessa pasta.
            NativeResolver.Install(cfg.IdeDir);
            Log.Info(Tag, $"ToolBridge: IDE em {cfg.IdeDir}");
        }
        catch (Exception e)
        {
            Log.Error(Tag, "ToolIntegration.ConfigureService: " + e);
        }
    }

    /// <summary>Grava ide/packages/gmide-project-tool-&lt;rid&gt;/@gm-tools.project-tool-&lt;rid&gt;.package.json.</summary>
    public static string RegisterProjectToolPackage(string ideDir, string stubPath, string rid)
    {
        var name = "@gm-tools/project-tool-" + rid;
        var dir = Path.Combine(ideDir, "packages", "gmide-project-tool-" + rid);
        Directory.CreateDirectory(dir);
        // Parte do package.json original (mesma versao, para a IDE nao oferecer atualizacao).
        var orig = Path.Combine(ideDir, ToolBridge.ProjectToolDirRel, "@gm-tools.project-tool-win-x64.package.json");
        JsonObject pkg;
        try { pkg = File.Exists(orig) ? JsonNode.Parse(File.ReadAllText(orig)) as JsonObject ?? new() : new(); }
        catch { pkg = new(); }
        pkg["name"] = name;
        pkg["version"] ??= "2024.14.165";
        pkg["description"] = "ProjectTool (win-x64 .NET) rodando no processo :build via libgmexec.so - GmIde";
        pkg["files"] = new JsonArray("*");
        pkg.Remove("publishConfig");
        pkg["bin"] = new JsonObject { ["projecttool"] = stubPath };
        pkg["gm"] = new JsonObject
        {
            ["destination"] = "gm-tools/project-tool-" + rid,
            ["tool"] = new JsonObject { ["path"] = stubPath },
        };
        var file = Path.Combine(dir, "@gm-tools.project-tool-" + rid + ".package.json");
        File.WriteAllText(file, pkg.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Log.Info(Tag, $"ProjectTool: pacote {name} -> {stubPath}");
        return file;
    }

    /// <summary>GML.lspConfig: "path" = stub, "args" = "--gmexec-tool=GameMakerLanguageServer " + args.</summary>
    public static bool RewriteLspConfig(string ideDir, string stubPath)
    {
        var cfg = Path.Combine(ideDir, ToolBridge.LspConfigRel);
        var orig = cfg + ".original";
        if (!File.Exists(orig))
        {
            if (!File.Exists(cfg)) { Log.Warn(Tag, $"{cfg} nao existe: Language Server desligado"); return false; }
            File.Copy(cfg, orig);
        }
        if (JsonNode.Parse(File.ReadAllText(orig)) is not JsonObject o) return false;
        var path = o["path"]?.GetValue<string>() ?? "";
        if (!string.Equals(Path.GetFileNameWithoutExtension(path), ToolBridge.LspToolName, StringComparison.OrdinalIgnoreCase))
        {
            Log.Warn(Tag, $"GML.lspConfig com path inesperado '{path}'; nao alterado");
            return false;
        }
        o["path"] = stubPath;
        o["args"] = ToolBridge.ToolArgPrefix + ToolBridge.LspToolName + " " + (o["args"]?.GetValue<string>() ?? "");
        File.WriteAllText(cfg, o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Log.Info(Tag, "Language Server: GML.lspConfig -> " + stubPath);
        return true;
    }
}
