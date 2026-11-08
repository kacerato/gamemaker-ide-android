using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GmIde;

// File operations shared by the Android exporter and the host integration scenario.
// Each request owns its entire compiler cache, output and temporary directory.
internal static class WebExportPackage
{
    internal sealed record Job(string Root, string BuildFile, string Archive, string Preview);

    internal static Job Prepare(string templateFile, string jobsDirectory, string displayName, string version)
    {
        var env = JsonNode.Parse(File.ReadAllText(templateFile))!.AsObject();
        var macros = JsonNode.Parse(File.ReadAllText(env["macros"]!.GetValue<string>()))!.AsObject();
        string root = Path.Combine(jobsDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string output = Path.Combine(root, "output"), temp = Path.Combine(root, "temp");
        Directory.CreateDirectory(output); Directory.CreateDirectory(temp);
        macros["asset_compiler_cache_directory"] = Path.Combine(root, "cache");
        macros["project_cache_directory_name"] = "project";
        macros["temp_directory"] = temp;
        string macroFile = Path.Combine(root, "macros.json");
        File.WriteAllText(macroFile, macros.ToJsonString());
        string options = Path.Combine(root, "targetoptions.json");
        File.WriteAllText(options, "{\"runtime\":\"VM\"}");
        env["macros"] = macroFile;
        env["targetOptions"] = options;
        env["outputFolder"] = output;
        env["tempFolder"] = temp;
        env["tempFolderUnmapped"] = temp;
        env["compile_output_file_name"] = Path.Combine(output, env["projectName"]!.GetValue<string>() + ".win");
        env["targetFile"] = Path.Combine(root, "game.zip");
        env["package_type"] = "OperaGXPackage_Zip";
        env["opera_gx_package_version"] = version;
        env["package_launch"] = "False";
        env["debug"] = "False";
        env["rerun"] = "False";
        env["targetMask"] = "17179869184";
        string build = Path.Combine(root, "build.bff");
        File.WriteAllText(build, env.ToJsonString());
        return new Job(root, build, Path.Combine(root, "game.zip"), Path.Combine(root, "preview"));
    }

    internal static void UnpackAndPersonalize(Job job, string title, string version, byte[]? iconPng)
    {
        if (!File.Exists(job.Archive)) throw new InvalidDataException("O compilador terminou sem gerar o pacote web.");
        // The archive is compiler output, but reject traversal and duplicate names before extracting.
        using (var zip = ZipFile.OpenRead(job.Archive))
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in zip.Entries)
            {
                string relative = entry.FullName.Replace('\\', '/');
                if (relative.StartsWith('/') || relative.Contains(':') || relative.Split('/').Any(p => p == "..") || !seen.Add(relative))
                    throw new InvalidDataException("Caminho invalido ou repetido no pacote web: " + relative);
            }
            zip.ExtractToDirectory(job.Preview);
        }
        foreach (string file in new[] { "index.html", "runner.js", "runner.wasm", "runner.data", "game.unx" })
            if (!File.Exists(Path.Combine(job.Preview, file)) || new FileInfo(Path.Combine(job.Preview, file)).Length == 0)
                throw new InvalidDataException("O pacote web esta incompleto: " + file);
        using (var wasm = File.OpenRead(Path.Combine(job.Preview, "runner.wasm")))
        {
            Span<byte> magic = stackalloc byte[4]; wasm.ReadExactly(magic);
            if (!magic.SequenceEqual(new byte[] { 0, 97, 115, 109 })) throw new InvalidDataException("Runner WebAssembly invalido.");
        }
        string htmlPath = Path.Combine(job.Preview, "index.html");
        string html = File.ReadAllText(htmlPath);
        if (!html.Contains("pauseMenu.hidden = false;") || !html.Contains("} else if (isMultiplayer()) {\n          resume();"))
        {
            // The runtime ships CRLF on some hosts. Normalize only line endings before matching.
            html = html.Replace("\r\n", "\n");
            if (!html.Contains("pauseMenu.hidden = false;") || !html.Contains("} else if (isMultiplayer()) {\n          resume();"))
                throw new InvalidDataException("Modelo web mudou: nao foi possivel adaptar a pausa/retomada do runner.");
        }
        html = html.Replace("pauseMenu.hidden = false;", "pauseMenu.hidden = true;")
            .Replace("} else if (isMultiplayer()) {\n          resume();", "} else {\n          resume();")
            .Replace("function resume() {", "function resume() {\n        if (typeof GM_unpause !== 'function' || !canvasElement.classList.contains('active')) return;");
        string metadata = "<title>" + WebUtility.HtmlEncode(title) + "</title><meta name=\"game-version\" content=\"" + WebUtility.HtmlEncode(version) + "\">";
        // The shipped GX page keeps a desktop debug console below the canvas. Release
        // exports occupy the viewport; its own ensureAspectRatio() keeps input mapping.
        metadata += "<style>html,body{width:100%;height:100%;min-height:0!important;min-width:0!important;overflow:hidden!important}#output-container,#multiplayer-stats-container{display:none!important}</style>";
        html = Regex.Replace(html, "<title\\b[^>]*>.*?</title>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (iconPng != null)
        {
            html = Regex.Replace(html, "<link\\b[^>]*rel=[\"'](?:shortcut )?icon[\"'][^>]*>", "", RegexOptions.IgnoreCase);
            File.WriteAllBytes(Path.Combine(job.Preview, "game-icon.png"), iconPng);
            metadata += "<link rel=\"icon\" type=\"image/png\" href=\"game-icon.png\">";
        }
        int head = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        if (head < 0) throw new InvalidDataException("O modelo web nao possui cabecalho HTML.");
        html = html.Insert(head, metadata);
        int body = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        if (body >= 0) html = html.Insert(body, "<script>if(window.chrome&&window.chrome.webview){window.quitIfSupported=function(){window.chrome.webview.postMessage('gmide-close');};}</script>");
        File.WriteAllText(htmlPath, html, new UTF8Encoding(false));
        // Package() renamed runner.html. Keep runner.json consistent for distribution tools.
        string manifestPath = Path.Combine(job.Preview, "runner.json");
        if (File.Exists(manifestPath))
        {
            var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
            var names = new JsonArray(); var hashes = new JsonArray();
            foreach (string file in Directory.EnumerateFiles(job.Preview, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                if (file == manifestPath) continue;
                names.Add(Path.GetRelativePath(job.Preview, file).Replace('\\', '/'));
                using var input = File.OpenRead(file);
                hashes.Add(Convert.ToHexString(MD5.HashData(input)).ToLowerInvariant());
            }
            manifest["manifestFiles"] = names; manifest["manifestFilesMD5"] = hashes;
            File.WriteAllText(manifestPath, manifest.ToJsonString());
        }
    }

    internal static void Publish(string folder, string output)
    {
        if (!output.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("A exportacao deve ser salva como .zip.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        string temporary = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { ZipFile.CreateFromDirectory(folder, temporary); File.Move(temporary, output, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
