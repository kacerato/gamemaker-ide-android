using System.Buffers.Binary;
using System.Text;

namespace GmIde;

// Adds a resource section to our unsigned, resource-free x64 player template.
// Uses PE data structures directly, so icon/version customization also runs on Android.
internal static class WindowsPlayerResources
{
    internal static void Write(string executable, string title, string version, byte[]? iconPng)
    {
        byte[] original = File.ReadAllBytes(executable);
        int Read16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(original.AsSpan(offset, 2));
        int Read32(int offset) => BinaryPrimitives.ReadInt32LittleEndian(original.AsSpan(offset, 4));
        if (original.Length < 256 || original[0] != 'M' || original[1] != 'Z') throw new InvalidDataException("Modelo EXE invalido.");
        int pe = Read32(60);
        if (pe < 64 || pe + 24 > original.Length || Read32(pe) != 0x4550 || Read16(pe + 4) != 0x8664)
            throw new InvalidDataException("Modelo EXE deve ser Windows x64.");
        int count = Read16(pe + 6), optional = pe + 24;
        int table = optional + Read16(pe + 20), newHeader = table + count * 40;
        if (Read16(optional) != 0x20b || Read32(optional + 112 + 16) != 0)
            throw new InvalidDataException("Modelo EXE inesperado: recursos existentes ou formato diferente.");
        int alignment = Read32(optional + 32), fileAlignment = Read32(optional + 36);
        if (alignment <= 0 || fileAlignment <= 0 || count < 1 || count > 96 || newHeader + 40 > Read32(optional + 60))
            throw new InvalidDataException("Cabecalho do modelo EXE nao tem espaco para os recursos.");
        int firstRaw = int.MaxValue, endVirtual = 0;
        for (int n = 0; n < count; n++)
        {
            int section = table + n * 40;
            if (Read32(section + 20) > 0) firstRaw = Math.Min(firstRaw, Read32(section + 20));
            endVirtual = Math.Max(endVirtual, checked(Read32(section + 12) + Math.Max(Read32(section + 8), Read32(section + 16))));
        }
        if (newHeader + 40 > firstRaw) throw new InvalidDataException("Cabecalho PE sobrepoe os dados.");
        int rva = Align(endVirtual, alignment), raw = Align(original.Length, fileAlignment);
        var resources = new SortedDictionary<int, byte[]> { [16] = Version(title, version, Path.GetFileName(executable)) };
        if (iconPng != null)
        {
            resources[3] = iconPng;
            using var stream = new MemoryStream(); using var group = new BinaryWriter(stream);
            group.Write((ushort)0); group.Write((ushort)1); group.Write((ushort)1);
            group.Write((byte)0); group.Write((byte)0); group.Write((byte)0); group.Write((byte)0);
            group.Write((ushort)1); group.Write((ushort)32); group.Write(iconPng.Length); group.Write((ushort)1);
            resources[14] = stream.ToArray();
        }
        int rootSize = 16 + resources.Count * 8, dataOffset = rootSize + resources.Count * 64;
        using var blob = new MemoryStream(); using var writer = new BinaryWriter(blob);
        blob.SetLength(dataOffset);
        void Put(int offset, uint value) { blob.Position = offset; writer.Write(value); }
        void DirectoryHeader(int offset, ushort entries) { blob.Position = offset + 14; writer.Write(entries); }
        DirectoryHeader(0, (ushort)resources.Count);
        int i = 0;
        foreach (var (type, bytes) in resources)
        {
            int nameDirectory = rootSize + i * 64, languageDirectory = nameDirectory + 24, dataEntry = nameDirectory + 48;
            Put(16 + i * 8, (uint)type); Put(20 + i * 8, (uint)nameDirectory | 0x80000000);
            DirectoryHeader(nameDirectory, 1); Put(nameDirectory + 16, 1); Put(nameDirectory + 20, (uint)languageDirectory | 0x80000000);
            DirectoryHeader(languageDirectory, 1); Put(languageDirectory + 16, 1033); Put(languageDirectory + 20, (uint)dataEntry);
            int data = Align((int)blob.Length, 4);
            Put(dataEntry, (uint)(rva + data)); Put(dataEntry + 4, (uint)bytes.Length);
            blob.Position = data; writer.Write(bytes); i++;
        }
        byte[] resourceBytes = blob.ToArray(), result = new byte[raw + Align(resourceBytes.Length, fileAlignment)];
        original.CopyTo(result, 0); resourceBytes.CopyTo(result, raw);
        void Write32(int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset, 4), value);
        Encoding.ASCII.GetBytes(".gmrsrc").CopyTo(result, newHeader);
        Write32(newHeader + 8, resourceBytes.Length); Write32(newHeader + 12, rva);
        Write32(newHeader + 16, Align(resourceBytes.Length, fileAlignment)); Write32(newHeader + 20, raw);
        Write32(newHeader + 36, 0x40000040); // initialized, readable data
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(pe + 6, 2), (ushort)(count + 1));
        Write32(optional + 56, Align(rva + resourceBytes.Length, alignment));
        Write32(optional + 8, checked(Read32(optional + 8) + Align(resourceBytes.Length, fileAlignment)));
        Write32(optional + 112 + 16, rva); Write32(optional + 112 + 20, resourceBytes.Length);
        Write32(optional + 64, 0); // no stale image checksum
        File.WriteAllBytes(executable, result);
    }

    static int Align(int value, int alignment) => checked((value + alignment - 1) / alignment * alignment);

    static byte[] Node(string name, ushort type, byte[] value, ushort valueLength, params byte[][] children)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.Unicode);
        writer.Write((ushort)0); writer.Write(valueLength); writer.Write(type);
        writer.Write(Encoding.Unicode.GetBytes(name + "\0"));
        void Pad() { while (stream.Position % 4 != 0) writer.Write((byte)0); }
        Pad(); writer.Write(value);
        foreach (byte[] child in children) { Pad(); writer.Write(child); }
        if (stream.Length > ushort.MaxValue) throw new InvalidDataException("Recursos de versao muito grandes.");
        stream.Position = 0; writer.Write((ushort)stream.Length);
        return stream.ToArray();
    }

    static byte[] Version(string title, string version, string filename)
    {
        var parts = version.Split('.');
        if (parts.Length > 4 || parts.Any(p => !ushort.TryParse(p, out _))) throw new ArgumentException("Versao invalida.");
        var v = parts.Select(ushort.Parse).Concat(Enumerable.Repeat((ushort)0, 4)).Take(4).ToArray();
        uint major = (uint)v[0] << 16 | v[1], minor = (uint)v[2] << 16 | v[3];
        using var stream = new MemoryStream(); using var fixedInfo = new BinaryWriter(stream);
        foreach (uint field in new uint[] { 0xFEEF04BD, 0x10000, major, minor, major, minor, 0x3f, 0, 0x40004, 1, 0, 0, 0 }) fixedInfo.Write(field);
        byte[] Text(string key, string value) => Node(key, 1, Encoding.Unicode.GetBytes(value + "\0"), (ushort)(value.Length + 1));
        byte[] table = Node("040904B0", 1, Array.Empty<byte>(), 0, Text("FileDescription", title), Text("ProductName", title),
            Text("FileVersion", version), Text("ProductVersion", version), Text("OriginalFilename", filename));
        byte[] strings = Node("StringFileInfo", 1, Array.Empty<byte>(), 0, table);
        byte[] variables = Node("VarFileInfo", 1, Array.Empty<byte>(), 0, Node("Translation", 0, new byte[] { 9, 4, 176, 4 }, 4));
        return Node("VS_VERSION_INFO", 0, stream.ToArray(), 52, strings, variables);
    }
}
