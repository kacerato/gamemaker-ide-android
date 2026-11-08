using System.Text;
using System.Text.RegularExpressions;

namespace GmIde;

internal static class WindowsWebPackage
{
    // Only file operations: also exercised with the actual x64 player on Windows.
    internal static string Create(string root, string webFolder, string playerFolder, string title, string id,
        string version, byte[]? iconPng, int width, int height)
    {
        if (!Regex.IsMatch(id, @"^[A-Za-z][A-Za-z0-9_.-]{2,199}$")) throw new ArgumentException("Identificador de armazenamento invalido.");
        if (title.Length == 0 || title.Length > 200 || title.Any(char.IsControl) || title.Contains('"'))
            throw new ArgumentException("Nome invalido: use ate 200 caracteres sem aspas ou quebras de linha.");
        string folder = Path.Combine(root, "windows"), web = Path.Combine(folder, "web");
        Directory.CreateDirectory(web);
        foreach (string file in Directory.EnumerateFiles(webFolder, "*", SearchOption.AllDirectories))
        {
            string dest = Path.Combine(web, Path.GetRelativePath(webFolder, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!); File.Copy(file, dest);
        }
        string exeName = new string(title.Select(c => c < 32 || "<>:\"/\\|?*".Contains(c) ? '_' : c).ToArray()).TrimEnd(' ', '.');
        // Avoid reserved DOS names even when a game's title is CON, AUX, etc.
        if (exeName.Length == 0 || Regex.IsMatch(exeName, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase)) exeName = "Game";
        foreach (string file in new[] { "GmGame.exe", "WebView2Loader.dll", "WebView2-LICENSE.txt", "miniz-LICENSE.txt" })
        {
            string source = Path.Combine(playerFolder, file);
            if (!File.Exists(source)) throw new FileNotFoundException("Modelo Windows incompleto: " + file);
            File.Copy(source, Path.Combine(folder, file == "GmGame.exe" ? exeName + ".exe" : file));
        }
        WindowsPlayerResources.Write(Path.Combine(folder, exeName + ".exe"), title, version, iconPng);
        File.WriteAllText(Path.Combine(folder, "game.ini"), $"[game]\r\ntitle={title}\r\nid={id}\r\nversion={version}\r\nwidth={Math.Clamp(width, 320, 7680)}\r\nheight={Math.Clamp(height, 240, 4320)}\r\n", Encoding.Unicode);
        if (iconPng != null)
        {
            File.WriteAllBytes(Path.Combine(folder, "game.ico"), IconFile(iconPng));
        }
        File.WriteAllText(Path.Combine(folder, "LEIA-ME.txt"),
            "Abra o EXE exportado. Os arquivos do jogo estao embutidos e sao preparados automaticamente.\r\n\r\n" +
            "Windows x64, Microsoft Edge WebView2 Runtime. Nao exige .NET, IDE ou servidor.\r\n" +
            "Se o WebView2 estiver ausente, o player oferece abrir o site oficial da Microsoft.\r\n" +
            "F11: tela cheia. Fechar a janela encerra o jogo.\r\n" +
            "Dados salvos: %LOCALAPPDATA%\\GmGames\\" + id + "\r\n" +
            "Mantenha o mesmo identificador ao atualizar para preservar os saves.\r\n" +
            "Este pacote executa WebAssembly em WebView2; nao e Windows VM/YYC nativo.\r\n", new UTF8Encoding(false));
        return folder;
    }

    internal static byte[] IconFile(byte[] png)
    {
        using var stream = new MemoryStream(); using var icon = new BinaryWriter(stream);
        icon.Write((ushort)0); icon.Write((ushort)1); icon.Write((ushort)1);
        icon.Write((byte)0); icon.Write((byte)0); icon.Write((byte)0); icon.Write((byte)0);
        icon.Write((ushort)1); icon.Write((ushort)32); icon.Write(png.Length); icon.Write(22); icon.Write(png);
        return stream.ToArray();
    }
}
