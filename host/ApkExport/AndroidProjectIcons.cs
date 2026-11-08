using System.Reflection;
using System.Security.Cryptography;

namespace GmIde;

internal static class AndroidProjectIcons
{
    static readonly string[] Densities = { "ldpi", "mdpi", "hdpi", "xhdpi", "xxhdpi", "xxxhdpi" };

    // Arquivos pertencem ao projeto/configuracao. Nomes por conteudo preservam undo
    // e permitem repetir um registro pendente sem sobrescrever imagens anteriores.
    internal static void Import(object options, string source, Func<string, string> expand,
        Func<string, string, byte[]> render)
    {
        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))).ToLowerInvariant();
        string folder = (string)options.GetType().GetMethod("GetConfigFilePath")!
            .Invoke(options, new object[] { "gmide-icons/v1-" + hash })!;
        string absolute = expand(folder);
        Directory.CreateDirectory(absolute);
        var images = new List<(PropertyInfo property, string reference)>();
        foreach (var density in Densities)
        foreach (bool adaptive in new[] { false, true })
        {
            string property = "option_android_icon_" + (adaptive ? "adaptive_" : "") + density;
            var p = options.GetType().GetProperty(property) ?? throw new MissingMemberException(property);
            string file = (adaptive ? "adaptive-" : "") + density + ".png";
            string path = Path.Combine(absolute, file);
            if (!File.Exists(path))
            {
                byte[] png = render(source, "drawable-" + density + "/" + (adaptive ? "adaptive_icon.png" : "icon.png"));
                File.WriteAllBytes(path + ".tmp", png);
                File.Move(path + ".tmp", path, overwrite: true);
            }
            images.Add((p, folder.TrimEnd('/', '\\') + "/" + file));
        }
        // So troca as propriedades depois de produzir todas as densidades.
        foreach (var image in images) image.property.SetValue(options, image.reference);
    }
}
