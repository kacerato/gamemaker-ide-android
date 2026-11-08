using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml;

namespace GmIde;

/// <summary>tools/apk-template/out/template.json (gerado por make-template.py no PC).</summary>
sealed class ApkTemplateInfo
{
    [JsonPropertyName("runtime")] public string Runtime { get; set; } = "";
    [JsonPropertyName("package")] public string Package { get; set; } = "";
    [JsonPropertyName("app_name_placeholder")] public string AppNamePlaceholder { get; set; } = "";
    [JsonPropertyName("min_sdk")] public int MinSdk { get; set; }
    [JsonPropertyName("target_sdk")] public int TargetSdk { get; set; }
    [JsonPropertyName("resource_ids")] public Dictionary<string, uint> ResourceIds { get; set; } = new();
    [JsonPropertyName("res_files")] public Dictionary<string, string> ResFiles { get; set; } = new();
    [JsonPropertyName("manifest_additions")] public ManifestAdditions Additions { get; set; } = new();

    public sealed class ManifestAdditions
    {
        [JsonPropertyName("manifest")] public List<string> Manifest { get; set; } = new();
        [JsonPropertyName("application")] public List<string> Application { get; set; } = new();
        [JsonPropertyName("application_attributes")] public Dictionary<string, string> ApplicationAttributes { get; set; } = new();
        [JsonPropertyName("manifest_attributes")] public Dictionary<string, string> ManifestAttributes { get; set; } = new();
    }

    public static ApkTemplateInfo Load(string path) =>
        JsonSerializer.Deserialize<ApkTemplateInfo>(File.ReadAllText(path)) ?? throw new InvalidDataException(path);
}

/// <summary>Valores que a tela "Gerar APK" pode trocar sem mexer no projeto (null = do projeto).</summary>
sealed class ApkOverrides
{
    public string? DisplayName { get; set; }
    public string? PackageName { get; set; }
    public string? VersionName { get; set; }
    public int? VersionCode { get; set; }
}

sealed class ApkBuildRequest
{
    /// <summary>Modulo Gradle expandido pelo Igor (contem src/main/AndroidManifest.xml).</summary>
    public required string ModuleDir { get; init; }
    public required string TemplateApk { get; init; }
    public required ApkTemplateInfo Template { get; init; }
    public required string OutApk { get; init; }
    public required IApkSigningKey Key { get; init; }
    public ApkOverrides? Overrides { get; init; }
    /// <summary>Imagem opcional escolhida na tela APK; substitui os PNGs do modulo sem altera-lo.</summary>
    public string? IconFile { get; init; }
    public Action<string> Log { get; init; } = _ => { };
}

/// <summary>
/// Monta o APK do jogo sem Gradle (docs/apk-export.md): o Igor ja expandiu o projeto Gradle do
/// runner (manifesto, strings, icones, assets/game.droid, jniLibs). Daqui saem so os dados do
/// jogo; codigo (classes.dex) e recursos compilados (resources.arsc + res/) vem do APK modelo,
/// compilado no PC com o pacote Java fixo. O manifesto e recompilado (Axml) com os nomes de
/// classe apontando para o pacote do modelo e o package/applicationId do jogo.
/// </summary>
static class ApkBuilder
{
    // Extensoes que o AGP guarda sem compressao (aaptOptions.noCompress padrao).
    static readonly HashSet<string> NoCompress = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".wav", ".mp2", ".mp3", ".ogg", ".aac", ".mpg", ".mpeg", ".mid",
        ".midi", ".smf", ".jet", ".rtttl", ".imy", ".xmf", ".mp4", ".m4a", ".m4v", ".3gp", ".3gpp", ".3g2",
        ".3gpp2", ".amr", ".awb", ".wma", ".wmv", ".webm", ".mkv", ".webp",
    };
    static readonly string[] ComponentTags = { "application", "activity", "activity-alias", "service", "receiver", "provider" };
    static readonly Regex PackageRe = new(@"^[a-zA-Z][a-zA-Z0-9_]*(\.[a-zA-Z][a-zA-Z0-9_]*)+$");

    public static bool IsValidPackage(string p) => PackageRe.IsMatch(p);

    public static void Build(ApkBuildRequest r)
    {
        var log = r.Log;
        var main = Path.Combine(r.ModuleDir, "src", "main");
        var manifestPath = Path.Combine(main, "AndroidManifest.xml");
        if (!File.Exists(manifestPath)) throw new FileNotFoundException("manifesto do projeto nao encontrado", manifestPath);

        var doc = new XmlDocument { PreserveWhitespace = false };
        doc.Load(manifestPath);
        var gradle = ReadGradle(Path.Combine(r.ModuleDir, "build.gradle"));
        string displayName = r.Overrides?.DisplayName ?? ReadAppName(Path.Combine(main, "res", "values", "strings.xml")) ?? "Game";
        var finalPkg = TransformManifest(doc, r.Template, gradle, r.Overrides, log);
        var axml = Axml.Compile(doc, name => r.Template.ResourceIds.TryGetValue(name, out var id) ? id : null, log);
        log($"Manifesto: pacote {finalPkg}, nome \"{displayName}\", {axml.Length} bytes");

        var tmp = r.OutApk + ".unsigned";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(r.OutApk))!);
        int assets = 0, libs = 0, icons = 0;
        long assetBytes = 0;
        using (var tpl = new ApkZipReader(r.TemplateApk))
        using (var zip = new ApkZipWriter(tmp))
        {
            zip.Add("AndroidManifest.xml", axml, compress: true);
            // icones: caminho no APK -> arquivo no projeto Gradle do jogo
            var iconFor = r.Template.ResFiles.ToDictionary(kv => kv.Value, kv => Path.Combine(main, "res", kv.Key.Replace('/', Path.DirectorySeparatorChar)));
            foreach (var e in tpl.Entries)
            {
                var n = e.Name;
                if (n == "AndroidManifest.xml" || n.StartsWith("lib/", StringComparison.Ordinal) || IsSignatureFile(n)) continue;
                if (n == "resources.arsc")
                {
                    var arsc = Arsc.ReplaceGlobalStrings(tpl.Read(e),
                        new Dictionary<string, string> { [r.Template.AppNamePlaceholder] = displayName }, out int rep);
                    if (rep == 0) log("Aviso: nome do app nao encontrado no resources.arsc do modelo");
                    zip.Add(n, arsc, compress: false, align: 4);
                    continue;
                }
                if (iconFor.TryGetValue(n, out var icon) &&
                    (File.Exists(icon) || (r.IconFile is { Length: > 0 } && IsIconResource(icon))))
                {
                    byte[]? custom = r.IconFile is { Length: > 0 } && IsIconResource(icon)
                        ? RenderIcon(r.IconFile, icon)
                        : null;
                    zip.Add(n, custom ?? File.ReadAllBytes(icon), compress: e.Method == 8, align: 4);
                    icons++;
                    continue;
                }
                zip.AddRaw(n, e.Method, e.Crc, e.CompressedSize, e.Size, tpl.ReadRaw(e), align: 4);
            }
            // assets do jogo (game.droid, options.ini, splash, arquivos incluidos...)
            var assetsDir = Path.Combine(main, "assets");
            if (Directory.Exists(assetsDir))
            {
                foreach (var f in Directory.EnumerateFiles(assetsDir, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
                {
                    var rel = "assets/" + Path.GetRelativePath(assetsDir, f).Replace('\\', '/');
                    if (zip.Contains(rel)) continue;
                    using var s = File.OpenRead(f);
                    assetBytes += s.Length;
                    zip.Add(rel, s, compress: !NoCompress.Contains(Path.GetExtension(f)), align: 4);
                    assets++;
                }
            }
            if (!zip.Contains("assets/game.droid"))
                throw new InvalidDataException("assets/game.droid nao foi gerado (a compilacao do jogo falhou?)");
            // bibliotecas nativas (libyoyo.so do runtime + extensoes): src/main/jniLibs e elibs
            foreach (var libRoot in new[] { Path.Combine(main, "jniLibs"), Path.Combine(r.ModuleDir, "elibs") })
            {
                if (!Directory.Exists(libRoot)) continue;
                foreach (var abiDir in Directory.EnumerateDirectories(libRoot).OrderBy(d => d, StringComparer.Ordinal))
                foreach (var so in Directory.EnumerateFiles(abiDir, "*.so").OrderBy(f => f, StringComparer.Ordinal))
                {
                    var rel = $"lib/{Path.GetFileName(abiDir)}/{Path.GetFileName(so)}";
                    if (zip.Contains(rel)) continue;
                    using var s = File.OpenRead(so);
                    zip.Add(rel, s, compress: false, align: 16384);
                    libs++;
                }
            }
            if (libs == 0) throw new InvalidDataException("nenhuma biblioteca nativa (libyoyo.so) no projeto Gradle");
            zip.Finish();
        }
        log($"APK: {assets} assets ({assetBytes / 1024} KiB), {libs} bibliotecas nativas, {icons} icones do projeto");
        ApkSignerV2.Sign(tmp, r.OutApk, r.Key);
        File.Delete(tmp);
        log($"Assinado (v2): {r.OutApk} ({new FileInfo(r.OutApk).Length / 1024} KiB)");
    }

    static bool IsIconResource(string path)
    {
        var p = path.Replace('\\', '/');
        return p.EndsWith("/icon.png", StringComparison.OrdinalIgnoreCase) ||
               p.EndsWith("/adaptive_icon.png", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Gera a densidade correta em memoria; icone adaptativo respeita a safe-zone 2/3.</summary>
    internal static byte[] RenderIcon(string source, string resourcePath)
    {
        var p = resourcePath.Replace('\\', '/');
        bool adaptive = p.EndsWith("/adaptive_icon.png", StringComparison.OrdinalIgnoreCase);
        int dp = p.Contains("xxxhdpi", StringComparison.Ordinal) ? 192
            : p.Contains("xxhdpi", StringComparison.Ordinal) ? 144
            : p.Contains("xhdpi", StringComparison.Ordinal) ? 96
            : p.Contains("hdpi", StringComparison.Ordinal) ? 72
            : p.Contains("ldpi", StringComparison.Ordinal) ? 36 : 48;
        int size = adaptive ? dp * 9 / 4 : dp;
        return RenderIconPng(source, size, adaptive);
    }

    internal static byte[] RenderIconPng(string source, int size, bool adaptive = false)
    {
        using var decoded = LoadIconBitmap(source, Math.Max(size, 432));
        using var output = global::Android.Graphics.Bitmap.CreateBitmap(size, size, global::Android.Graphics.Bitmap.Config.Argb8888!);
        using (var canvas = new global::Android.Graphics.Canvas(output))
        {
            canvas.DrawColor(global::Android.Graphics.Color.Transparent);
            float inset = adaptive ? size / 6f : 0f;
            var dst = new global::Android.Graphics.RectF(inset, inset, size - inset, size - inset);
            int side = Math.Min(decoded.Width, decoded.Height);
            int left = (decoded.Width - side) / 2, top = (decoded.Height - side) / 2;
            using var src = new global::Android.Graphics.Rect(left, top, left + side, top + side);
            using var paint = new global::Android.Graphics.Paint(global::Android.Graphics.PaintFlags.FilterBitmap | global::Android.Graphics.PaintFlags.AntiAlias);
            canvas.DrawBitmap(decoded, src, dst, paint);
        }
        using var stream = new MemoryStream();
        if (!output.Compress(global::Android.Graphics.Bitmap.CompressFormat.Png!, 100, stream))
            throw new IOException("Nao consegui converter a imagem para PNG.");
        return stream.ToArray();
    }

    static global::Android.Graphics.Bitmap LoadIconBitmap(string source, int minimumSize)
    {
        if (IcoImage.Read(source) is { } ico)
        {
            if (ico.Png != null) return global::Android.Graphics.BitmapFactory.DecodeByteArray(ico.Png, 0, ico.Png.Length)
                ?? throw new InvalidDataException("PNG do icone ICO invalido.");
            return global::Android.Graphics.Bitmap.CreateBitmap(ico.Pixels!, ico.Width, ico.Height, global::Android.Graphics.Bitmap.Config.Argb8888!);
        }
        var bounds = new global::Android.Graphics.BitmapFactory.Options { InJustDecodeBounds = true };
        global::Android.Graphics.BitmapFactory.DecodeFile(source, bounds)?.Recycle();
        int max = Math.Max(bounds.OutWidth, bounds.OutHeight);
        if (max <= 0) throw new InvalidDataException("A imagem escolhida nao e um arquivo de imagem valido.");
        int sample = 1;
        while (max / (sample * 2) >= minimumSize) sample *= 2;
        using var opts = new global::Android.Graphics.BitmapFactory.Options { InSampleSize = sample, InPreferredConfig = global::Android.Graphics.Bitmap.Config.Argb8888 };
        return global::Android.Graphics.BitmapFactory.DecodeFile(source, opts)
            ?? throw new InvalidDataException("Nao consegui abrir a imagem do icone.");
    }

    static bool IsSignatureFile(string n) =>
        n.StartsWith("META-INF/", StringComparison.Ordinal) &&
        (n == "META-INF/MANIFEST.MF" || n.EndsWith(".SF") || n.EndsWith(".RSA") || n.EndsWith(".DSA") || n.EndsWith(".EC"));

    internal sealed record GradleInfo(int? MinSdk, int? TargetSdk);

    internal static GradleInfo ReadGradle(string path)
    {
        if (!File.Exists(path)) return new GradleInfo(null, null);
        var t = File.ReadAllText(path);
        int? Num(string key)
        {
            var m = Regex.Match(t, key + @"\s+'?(\d+)'?");
            return m.Success ? int.Parse(m.Groups[1].Value) : null;
        }
        return new GradleInfo(Num("minSdkVersion"), Num("targetSdkVersion"));
    }

    /// <summary>app_name do strings.xml expandido pelo Igor (Utils.AndroidEscapeString desfeito).</summary>
    static string? ReadAppName(string path)
    {
        if (!File.Exists(path)) return null;
        var d = new XmlDocument();
        d.Load(path);
        foreach (XmlElement s in d.GetElementsByTagName("string"))
            if (s.GetAttribute("name") == "app_name") return UnescapeAndroid(s.InnerText);
        return null;
    }

    internal static string UnescapeAndroid(string s)
    {
        s = s.Trim();
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') s = s.Substring(1, s.Length - 2);
        var sb = new System.Text.StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                char c = s[++i];
                sb.Append(c switch { 'n' => '\n', 't' => '\t', _ => c });
            }
            else sb.Append(s[i]);
        }
        return sb.ToString();
    }

    /// <summary>Ajusta o manifesto expandido pelo Igor para o APK sem Gradle. Devolve o pacote final.</summary>
    internal static string TransformManifest(XmlDocument doc, ApkTemplateInfo tpl, GradleInfo g, ApkOverrides? ov, Action<string> log)
    {
        var manifest = doc.DocumentElement ?? throw new InvalidDataException("manifesto vazio");
        var origPkg = manifest.GetAttribute("package");
        var finalPkg = string.IsNullOrWhiteSpace(ov?.PackageName) ? origPkg : ov!.PackageName!.Trim();
        if (!IsValidPackage(finalPkg)) throw new InvalidDataException($"nome de pacote invalido: \"{finalPkg}\"");
        manifest.SetAttribute("package", finalPkg);
        if (ov?.VersionCode is int vc) manifest.SetAttribute("versionCode", Axml.AndroidNs, vc.ToString());
        if (!string.IsNullOrWhiteSpace(ov?.VersionName)) manifest.SetAttribute("versionName", Axml.AndroidNs, ov!.VersionName!.Trim());

        // Atributos/elementos "tools:" so servem ao merger do Gradle.
        foreach (XmlElement el in manifest.SelectNodes("//*")!.Cast<XmlElement>().Prepend(manifest).ToList())
        {
            foreach (var a in el.Attributes.Cast<XmlAttribute>().Where(a => a.NamespaceURI == Axml.ToolsNs || (a.Prefix == "xmlns" && a.LocalName == "tools")).ToList())
                el.Attributes.Remove(a);
        }

        // Classes: o codigo do modelo esta no pacote fixo (template.json "package").
        foreach (var tag in ComponentTags)
        foreach (XmlElement el in doc.GetElementsByTagName(tag))
        {
            foreach (var attr in new[] { "name", "targetActivity", "backupAgent" })
            {
                var v = el.GetAttribute(attr, Axml.AndroidNs);
                if (string.IsNullOrEmpty(v)) continue;
                string? nv = v.StartsWith('.') ? tpl.Package + v
                    : v.StartsWith(origPkg + ".", StringComparison.Ordinal) ? tpl.Package + v.Substring(origPkg.Length)
                    : null;
                if (nv != null) el.SetAttribute(attr, Axml.AndroidNs, nv);
            }
        }

        // uses-sdk: o build.gradle do jogo manda, mas v2 sem v1 exige minSdk >= 24.
        foreach (var old in doc.GetElementsByTagName("uses-sdk").Cast<XmlElement>().ToList()) old.ParentNode!.RemoveChild(old);
        var sdk = doc.CreateElement("uses-sdk");
        int min = Math.Max(g.MinSdk ?? tpl.MinSdk, tpl.MinSdk);
        int target = g.TargetSdk ?? tpl.TargetSdk;
        if (g.MinSdk is int gm && gm < tpl.MinSdk) log($"minSdk {gm} do projeto elevado para {tpl.MinSdk} (assinatura v2)");
        sdk.SetAttribute("minSdkVersion", Axml.AndroidNs, min.ToString());
        sdk.SetAttribute("targetSdkVersion", Axml.AndroidNs, target.ToString());
        manifest.PrependChild(sdk);

        // O que as bibliotecas androidx acrescentam no merge (provider do startup etc.).
        var app = manifest.GetElementsByTagName("application").Cast<XmlElement>().FirstOrDefault()
                  ?? throw new InvalidDataException("manifesto sem <application>");
        void SetMissing(XmlElement el, Dictionary<string, string> attrs)
        {
            foreach (var (k, v) in attrs)
            {
                if (k.StartsWith("android:"))
                {
                    var local = k.Substring(8);
                    if (!el.HasAttribute(local, Axml.AndroidNs)) el.SetAttribute(local, Axml.AndroidNs, v);
                }
                else if (!el.HasAttribute(k)) el.SetAttribute(k, v);
            }
        }
        SetMissing(app, tpl.Additions.ApplicationAttributes);
        SetMissing(manifest, tpl.Additions.ManifestAttributes);
        void AddFragments(XmlElement parent, IEnumerable<string> frags)
        {
            foreach (var f in frags)
            {
                var wrapped = $"<x xmlns:android=\"{Axml.AndroidNs}\">{f.Replace("${applicationId}", finalPkg)}</x>";
                var fd = new XmlDocument();
                fd.LoadXml(wrapped);
                var node = (XmlElement)fd.DocumentElement!.FirstChild!;
                var key = node.GetAttribute("name", Axml.AndroidNs);
                bool dup = parent.ChildNodes.OfType<XmlElement>().Any(c => c.LocalName == node.LocalName && c.GetAttribute("name", Axml.AndroidNs) == key);
                if (!dup) parent.AppendChild(doc.ImportNode(node, deep: true));
            }
        }
        AddFragments(manifest, tpl.Additions.Manifest);
        AddFragments(app, tpl.Additions.Application);
        return finalPkg;
    }
}
