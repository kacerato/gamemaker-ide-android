using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace GmIde;

// The same portable format is written on Android and consumed by bundle.cpp.
// Files are compressed separately so extraction needs only a 32 KiB dictionary.
internal static class WindowsSingleFilePackage
{
    internal static void Publish(string folder, string output)
    {
        string[] players = Directory.GetFiles(folder, "*.exe");
        if (players.Length != 1) throw new InvalidDataException("O pacote deve conter um player Windows.");
        string player = Path.GetFullPath(players[0]);
        var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(f => Path.GetFullPath(f) != player).OrderBy(f => f, StringComparer.Ordinal).ToArray();
        if (files.Length is 0 or > 100000) throw new InvalidDataException("Quantidade de arquivos invalida.");
        string temp = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        try
        {
            using (var dest = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite))
            {
                using (var source = File.OpenRead(player)) source.CopyTo(dest);
                long offset = dest.Position;
                using var writer = new BinaryWriter(dest, Encoding.UTF8, leaveOpen: true);
                writer.Write("GMBNDL01"u8); writer.Write((uint)files.Length);
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string file in files)
                {
                    string name = Path.GetRelativePath(folder, file).Replace('\\', '/');
                    ValidateName(name);
                    if (!names.Add(name)) throw new InvalidDataException("Nome de arquivo Windows duplicado: " + name);
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("Link nao permitido no pacote: " + name);
                    byte[] path = Encoding.UTF8.GetBytes(name);
                    writer.Write((uint)path.Length);
                    long header = dest.Position;
                    writer.Write(0UL); writer.Write(0UL); writer.Write(new byte[32]); writer.Write(path);
                    long start = dest.Position, size = 0;
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    using (var source = File.OpenRead(file))
                    using (var compressed = new DeflateStream(dest, CompressionLevel.Optimal, leaveOpen: true))
                    {
                        byte[] buffer = new byte[65536]; int read;
                        while ((read = source.Read(buffer)) != 0)
                        { hash.AppendData(buffer, 0, read); compressed.Write(buffer, 0, read); size += read; }
                    }
                    long end = dest.Position;
                    dest.Position = header;
                    writer.Write((ulong)size); writer.Write((ulong)(end - start)); writer.Write(hash.GetHashAndReset());
                    dest.Position = end;
                }
                long payloadLength = dest.Position - offset;
                dest.Position = offset;
                using var payloadHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] block = new byte[65536]; long remaining = payloadLength;
                while (remaining > 0)
                {
                    int read = dest.Read(block, 0, (int)Math.Min(block.Length, remaining));
                    if (read == 0) throw new EndOfStreamException();
                    payloadHash.AppendData(block, 0, read); remaining -= read;
                }
                writer.Write("GMIDESF1"u8); writer.Write((ulong)offset); writer.Write((ulong)payloadLength);
                writer.Write(payloadHash.GetHashAndReset());
                dest.Flush(flushToDisk: true);
            }
            File.Move(temp, output, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    internal static void ValidateName(string name)
    {
        if (Encoding.UTF8.GetByteCount(name) > 1024 || name.Length == 0)
            throw new InvalidDataException("Caminho Windows invalido.");
        foreach (string part in name.Split('/'))
        {
            string stem = part.Split('.')[0].ToUpperInvariant();
            if (part.Length == 0 || part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.') ||
                part.Any(c => c < 32 || "<>:\"\\|?*".Contains(c)) || stem is "CON" or "PRN" or "AUX" or "NUL" ||
                (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] is >= '1' and <= '9'))
                throw new InvalidDataException("Caminho Windows invalido: " + name);
        }
    }
}
