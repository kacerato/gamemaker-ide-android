using System.IO.Compression;
using System.Text;

namespace GmIde;

/// <summary>Entrada lida do diretorio central de um zip (APK modelo).</summary>
sealed record ZipEntryInfo(string Name, ushort Method, uint Crc, long CompressedSize, long Size, long LocalHeaderOffset);

/// <summary>Leitor minimo de zip (sem zip64): lista entradas e copia os dados crus (comprimidos).</summary>
sealed class ApkZipReader : IDisposable
{
    readonly FileStream _fs;
    public List<ZipEntryInfo> Entries { get; } = new();

    public ApkZipReader(string path)
    {
        _fs = File.OpenRead(path);
        long len = _fs.Length;
        int tail = (int)Math.Min(len, 22 + 0xFFFF);
        var buf = new byte[tail];
        _fs.Position = len - tail;
        _fs.ReadExactly(buf);
        int eocd = -1;
        for (int i = tail - 22; i >= 0; i--)
            if (BitConverter.ToUInt32(buf, i) == 0x06054b50) { eocd = i; break; }
        if (eocd < 0) throw new InvalidDataException($"{path}: fim do diretorio central nao encontrado");
        int count = BitConverter.ToUInt16(buf, eocd + 10);
        long cdOffset = BitConverter.ToUInt32(buf, eocd + 16);
        long cdSize = BitConverter.ToUInt32(buf, eocd + 12);
        var cd = new byte[cdSize];
        _fs.Position = cdOffset;
        _fs.ReadExactly(cd);
        int p = 0;
        for (int i = 0; i < count; i++)
        {
            if (BitConverter.ToUInt32(cd, p) != 0x02014b50) throw new InvalidDataException("diretorio central corrompido");
            ushort method = BitConverter.ToUInt16(cd, p + 10);
            uint crc = BitConverter.ToUInt32(cd, p + 16);
            uint csize = BitConverter.ToUInt32(cd, p + 20);
            uint usize = BitConverter.ToUInt32(cd, p + 24);
            int nlen = BitConverter.ToUInt16(cd, p + 28), xlen = BitConverter.ToUInt16(cd, p + 30), clen = BitConverter.ToUInt16(cd, p + 32);
            uint lho = BitConverter.ToUInt32(cd, p + 42);
            var name = Encoding.UTF8.GetString(cd, p + 46, nlen);
            Entries.Add(new ZipEntryInfo(name, method, crc, csize, usize, lho));
            p += 46 + nlen + xlen + clen;
        }
    }

    long DataOffset(ZipEntryInfo e)
    {
        var h = new byte[30];
        _fs.Position = e.LocalHeaderOffset;
        _fs.ReadExactly(h);
        if (BitConverter.ToUInt32(h, 0) != 0x04034b50) throw new InvalidDataException($"cabecalho local invalido: {e.Name}");
        return e.LocalHeaderOffset + 30 + BitConverter.ToUInt16(h, 26) + BitConverter.ToUInt16(h, 28);
    }

    /// <summary>Bytes crus (como estao no zip, comprimidos ou nao).</summary>
    public byte[] ReadRaw(ZipEntryInfo e)
    {
        var data = new byte[e.CompressedSize];
        _fs.Position = DataOffset(e);
        _fs.ReadExactly(data);
        return data;
    }

    /// <summary>Conteudo descomprimido.</summary>
    public byte[] Read(ZipEntryInfo e)
    {
        var raw = ReadRaw(e);
        if (e.Method == 0) return raw;
        if (e.Method != 8) throw new InvalidDataException($"metodo {e.Method} nao suportado: {e.Name}");
        using var ds = new DeflateStream(new MemoryStream(raw), CompressionMode.Decompress);
        var outMs = new MemoryStream((int)e.Size);
        ds.CopyTo(outMs);
        return outMs.ToArray();
    }

    public void Dispose() => _fs.Dispose();
}

/// <summary>
/// Escritor de zip para APK: entradas STORED alinhadas (campo extra 0xD935, como o zipalign
/// -P), datas fixas, sem data descriptor e sem zip64. As .so ficam STORED e alinhadas a 16 KiB
/// (paginas de 16 KiB do Android 15+ e extractNativeLibs=false), o resources.arsc a 4 bytes.
/// </summary>
sealed class ApkZipWriter : IDisposable
{
    const ushort DosTime = 0, DosDate = (1 << 5) | 1; // 1980-01-01, deterministico
    readonly FileStream _fs;
    readonly List<(byte[] name, ushort method, uint crc, uint csize, uint usize, uint offset)> _cd = new();
    readonly HashSet<string> _names = new(StringComparer.Ordinal);

    public ApkZipWriter(string path) => _fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16);

    public bool Contains(string name) => _names.Contains(name);

    /// <summary>Copia uma entrada ja comprimida de outro zip (sem recomprimir).</summary>
    public void AddRaw(string name, ushort method, uint crc, long csize, long usize, byte[] raw, int align = 0)
    {
        if (method != 0) align = 0;
        var nb = Header(name, method, align, out long start);
        WriteSizes(start, crc, (uint)csize, (uint)usize);
        _fs.Write(raw);
        _cd.Add((nb, method, crc, (uint)csize, (uint)usize, (uint)start));
    }

    public void Add(string name, byte[] data, bool compress, int align = 0) =>
        Add(name, new MemoryStream(data, writable: false), compress, align);

    public void Add(string name, Stream data, bool compress, int align = 0)
    {
        ushort method = compress ? (ushort)8 : (ushort)0;
        if (compress) align = 0;
        var nb = Header(name, method, align, out long start);
        long dataStart = _fs.Position;
        var crc = new Crc32();
        long usize = 0;
        var buf = new byte[1 << 16];
        if (compress)
        {
            using var ds = new DeflateStream(_fs, CompressionLevel.Optimal, leaveOpen: true);
            int n;
            while ((n = data.Read(buf, 0, buf.Length)) > 0) { crc.Update(buf.AsSpan(0, n)); ds.Write(buf, 0, n); usize += n; }
        }
        else
        {
            int n;
            while ((n = data.Read(buf, 0, buf.Length)) > 0) { crc.Update(buf.AsSpan(0, n)); _fs.Write(buf, 0, n); usize += n; }
        }
        long end = _fs.Position;
        long csize = end - dataStart;
        if (usize > uint.MaxValue || csize > uint.MaxValue || end > uint.MaxValue)
            throw new IOException($"{name}: arquivo grande demais para zip sem zip64 (> 4 GB)");
        WriteSizes(start, crc.Value, (uint)csize, (uint)usize);
        _fs.Position = end;
        _cd.Add((nb, method, crc.Value, (uint)csize, (uint)usize, (uint)start));
    }

    byte[] Header(string name, ushort method, int align, out long start)
    {
        if (!_names.Add(name)) throw new InvalidOperationException($"entrada repetida no APK: {name}");
        var nb = Encoding.UTF8.GetBytes(name);
        start = _fs.Position;
        int extra = 0;
        if (align > 1)
        {
            // campo 0xD935: id(2) tamanho(2) alinhamento(2) + zeros ate o dado cair no multiplo
            long dataPos = start + 30 + nb.Length + 6;
            int pad = (int)((align - dataPos % align) % align);
            extra = 6 + pad;
        }
        var w = new BinaryWriter(_fs, Encoding.UTF8, leaveOpen: true);
        w.Write(0x04034b50u);
        w.Write((ushort)(method == 0 ? 10 : 20));
        w.Write((ushort)0x0800); // nomes UTF-8
        w.Write(method);
        w.Write(DosTime); w.Write(DosDate);
        w.Write(0u); w.Write(0u); w.Write(0u); // crc/tamanhos (WriteSizes)
        w.Write((ushort)nb.Length);
        w.Write((ushort)extra);
        w.Write(nb);
        if (extra > 0)
        {
            w.Write((ushort)0xD935);
            w.Write((ushort)(extra - 4));
            w.Write((ushort)align);
            for (int i = 6; i < extra; i++) w.Write((byte)0);
        }
        w.Flush();
        return nb;
    }

    void WriteSizes(long headerStart, uint crc, uint csize, uint usize)
    {
        long cur = _fs.Position;
        _fs.Position = headerStart + 14;
        _fs.Write(BitConverter.GetBytes(crc));
        _fs.Write(BitConverter.GetBytes(csize));
        _fs.Write(BitConverter.GetBytes(usize));
        _fs.Position = cur;
    }

    public void Finish()
    {
        long cdStart = _fs.Position;
        var w = new BinaryWriter(_fs, Encoding.UTF8, leaveOpen: true);
        foreach (var e in _cd)
        {
            w.Write(0x02014b50u);
            w.Write((ushort)20);                               // made by
            w.Write((ushort)(e.method == 0 ? 10 : 20));        // needed
            w.Write((ushort)0x0800);
            w.Write(e.method);
            w.Write(DosTime); w.Write(DosDate);
            w.Write(e.crc); w.Write(e.csize); w.Write(e.usize);
            w.Write((ushort)e.name.Length);
            w.Write((ushort)0); w.Write((ushort)0);            // extra, comentario
            w.Write((ushort)0); w.Write((ushort)0); w.Write(0u); // disco, atributos
            w.Write(e.offset);
            w.Write(e.name);
        }
        long cdEnd = _fs.Position;
        if (_cd.Count > 0xFFFF) throw new IOException("entradas demais para zip sem zip64");
        w.Write(0x06054b50u);
        w.Write((ushort)0); w.Write((ushort)0);
        w.Write((ushort)_cd.Count); w.Write((ushort)_cd.Count);
        w.Write((uint)(cdEnd - cdStart));
        w.Write((uint)cdStart);
        w.Write((ushort)0);
        w.Flush();
        _fs.Flush();
    }

    public void Dispose() => _fs.Dispose();
}

/// <summary>CRC-32 (IEEE 802.3), por tabela.</summary>
sealed class Crc32
{
    static readonly uint[] Table = MakeTable();
    uint _crc = 0xFFFFFFFF;
    public uint Value => ~_crc;

    static uint[] MakeTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    public void Update(ReadOnlySpan<byte> data)
    {
        uint c = _crc;
        foreach (var b in data) c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        _crc = c;
    }
}
