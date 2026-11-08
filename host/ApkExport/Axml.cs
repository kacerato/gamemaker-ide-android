using System.Globalization;
using System.Text;
using System.Xml;

namespace GmIde;

/// <summary>
/// Compila um AndroidManifest.xml (texto) para o XML binario do Android (ResXMLTree), como o
/// aapt2 faria: pool de strings UTF-16 com os nomes de atributos com id primeiro + mapa de ids,
/// atributos ordenados por id, valores tipados conforme o formato do atributo (enum, flags,
/// boolean, inteiro, cor, float, referencia, string). Sem aapt2 no aparelho (docs/apk-export.md).
/// </summary>
static class Axml
{
    public const string AndroidNs = "http://schemas.android.com/apk/res/android";
    public const string ToolsNs = "http://schemas.android.com/tools";

    const ushort ResXmlType = 0x0003, StringPoolType = 0x0001, ResourceMapType = 0x0180,
        StartNamespaceType = 0x0100, EndNamespaceType = 0x0101, StartElementType = 0x0102, EndElementType = 0x0103;
    const byte TypeNull = 0x00, TypeReferenceV = 0x01, TypeStringV = 0x03, TypeFloatV = 0x04,
        TypeIntDec = 0x10, TypeIntHex = 0x11, TypeIntBoolean = 0x12,
        TypeArgb8 = 0x1c, TypeRgb8 = 0x1d, TypeArgb4 = 0x1e, TypeRgb4 = 0x1f;
    const uint NoIndex = 0xFFFFFFFF;

    sealed class Value { public byte Type; public uint Data; public string? Str; }
    sealed class AttrItem { public string? Ns; public string Name = ""; public uint ResId; public Value V = new(); }
    sealed class Elem { public string Name = ""; public List<AttrItem> Attrs = new(); public List<Elem> Children = new(); public int Line; }

    /// <param name="resolveRef">"string/app_name" -> id do recurso do app (null se nao existe).</param>
    /// <param name="warn">Avisos (atributo descartado por referencia inexistente etc.).</param>
    public static byte[] Compile(XmlDocument doc, Func<string, uint?> resolveRef, Action<string>? warn = null)
    {
        var root = doc.DocumentElement ?? throw new InvalidDataException("manifesto vazio");
        int line = 1;
        var tree = Convert(root, resolveRef, warn, ref line);

        // Pool: nomes de atributos com id (ordenados por id, sem repeticao) e depois o resto.
        var withId = new SortedDictionary<uint, string>();
        void CollectIds(Elem e)
        {
            foreach (var a in e.Attrs)
                if (a.ResId != 0)
                {
                    if (withId.TryGetValue(a.ResId, out var prev) && prev != a.Name)
                        throw new InvalidDataException($"id 0x{a.ResId:x8} com dois nomes ({prev}, {a.Name})");
                    withId[a.ResId] = a.Name;
                }
            foreach (var c in e.Children) CollectIds(c);
        }
        CollectIds(tree);
        var pool = new List<string>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var name in withId.Values) { index[name] = pool.Count; pool.Add(name); }
        var resIds = withId.Keys.ToArray();
        uint Str(string s)
        {
            if (!index.TryGetValue(s, out var i)) { i = pool.Count; pool.Add(s); index[s] = i; }
            return (uint)i;
        }
        // Ordem de entrada no pool: prefixo/uri, depois elementos e valores na ordem do documento.
        uint prefix = Str("android"), uri = Str(AndroidNs);
        var body = new MemoryStream();
        var w = new BinaryWriter(body);
        WriteNode(w, StartNamespaceType, 1, x => { x.Write(prefix); x.Write(uri); });
        void Emit(Elem e)
        {
            uint name = Str(e.Name);
            // aapt2 (XmlFlattener): com id primeiro em ordem de id; sem id por namespace e nome.
            var attrs = e.Attrs.OrderBy(a => a.ResId == 0 ? 1 : 0).ThenBy(a => a.ResId)
                .ThenBy(a => a.Ns ?? "", StringComparer.Ordinal).ThenBy(a => a.Name, StringComparer.Ordinal).ToList();
            var encoded = attrs.Select(a => (ns: a.Ns == null ? NoIndex : Str(a.Ns), name: Str(a.Name),
                raw: a.V.Str != null ? Str(a.V.Str) : NoIndex, a.V)).ToList();
            WriteNode(w, StartElementType, e.Line, x =>
            {
                x.Write(NoIndex);          // ns do elemento
                x.Write(name);
                x.Write((ushort)20);       // attributeStart
                x.Write((ushort)20);       // attributeSize
                x.Write((ushort)encoded.Count);
                x.Write((ushort)0); x.Write((ushort)0); x.Write((ushort)0); // id/class/style index
                foreach (var (ns, an, raw, v) in encoded)
                {
                    x.Write(ns); x.Write(an); x.Write(raw);
                    x.Write((ushort)8); x.Write((byte)0); x.Write(v.Type);
                    x.Write(v.Type == TypeStringV ? raw : v.Data);
                }
            });
            foreach (var c in e.Children) Emit(c);
            WriteNode(w, EndElementType, e.Line, x => { x.Write(NoIndex); x.Write(name); });
        }
        Emit(tree);
        WriteNode(w, EndNamespaceType, line, x => { x.Write(prefix); x.Write(uri); });
        w.Flush();

        var poolBytes = StringPool.EncodeUtf16(pool);
        var map = new MemoryStream();
        var mw = new BinaryWriter(map);
        mw.Write(ResourceMapType); mw.Write((ushort)8); mw.Write((uint)(8 + 4 * resIds.Length));
        foreach (var id in resIds) mw.Write(id);
        mw.Flush();

        var outStream = new MemoryStream();
        var ow = new BinaryWriter(outStream);
        ow.Write(ResXmlType); ow.Write((ushort)8);
        ow.Write((uint)(8 + poolBytes.Length + map.Length + body.Length));
        ow.Write(poolBytes); ow.Write(map.ToArray()); ow.Write(body.ToArray());
        ow.Flush();
        return outStream.ToArray();
    }

    static void WriteNode(BinaryWriter w, ushort type, int line, Action<BinaryWriter> ext)
    {
        var ms = new MemoryStream();
        var x = new BinaryWriter(ms);
        ext(x);
        x.Flush();
        // ResXMLTree_node: type, headerSize=16, size, lineNumber, comment
        w.Write(type); w.Write((ushort)16); w.Write((uint)(16 + ms.Length));
        w.Write((uint)line); w.Write(NoIndex);
        w.Write(ms.ToArray());
    }

    static Elem Convert(XmlElement x, Func<string, uint?> resolveRef, Action<string>? warn, ref int line)
    {
        var e = new Elem { Name = x.LocalName, Line = line++ };
        if (!string.IsNullOrEmpty(x.NamespaceURI))
            throw new InvalidDataException($"elemento com namespace nao suportado: {x.Name}");
        foreach (XmlAttribute a in x.Attributes)
        {
            if (a.Prefix == "xmlns" || a.Name == "xmlns") continue;
            if (a.NamespaceURI == ToolsNs) continue;
            var item = new AttrItem { Name = a.LocalName };
            if (a.NamespaceURI == AndroidNs)
            {
                item.Ns = AndroidNs;
                var def = ManifestAttrs.Get(a.LocalName);
                if (def != null) item.ResId = def.Id;
                var v = CompileValue(a.Value, def, resolveRef, out var err);
                if (v == null) { warn?.Invoke($"<{x.Name} {a.Name}=\"{a.Value}\">: {err}; atributo descartado"); continue; }
                item.V = v;
            }
            else if (string.IsNullOrEmpty(a.NamespaceURI))
            {
                // platformBuildVersionCode/Name: o aapt2 grava como inteiro
                item.V = a.LocalName.StartsWith("platformBuildVersion", StringComparison.Ordinal) && TryInt(a.Value, out var iv)
                    ? iv : new Value { Type = TypeStringV, Str = a.Value };
            }
            else
            {
                warn?.Invoke($"atributo {a.Name} ({a.NamespaceURI}) descartado");
                continue;
            }
            e.Attrs.Add(item);
        }
        foreach (XmlNode c in x.ChildNodes)
            if (c is XmlElement ce) e.Children.Add(Convert(ce, resolveRef, warn, ref line));
        return e;
    }

    static Value? CompileValue(string v, Attr? def, Func<string, uint?> resolveRef, out string? err)
    {
        err = null;
        var t = v.Trim();
        if (def == null) return new Value { Type = TypeStringV, Str = v }; // atributo desconhecido: string
        if (def.Enums != null && def.Enums.TryGetValue(t, out var ev)) return new Value { Type = TypeIntDec, Data = unchecked((uint)ev) };
        if (def.Flags != null && t.Length > 0)
        {
            uint acc = 0; bool all = true;
            foreach (var part in t.Split('|'))
            {
                if (def.Flags.TryGetValue(part.Trim(), out var fv)) acc |= fv; else { all = false; break; }
            }
            if (all) return new Value { Type = TypeIntHex, Data = acc };
        }
        if (t.Length == 0) return new Value { Type = TypeNull, Data = 1 }; // DATA_NULL_EMPTY
        if (t == "@null") return new Value { Type = TypeReferenceV, Data = 0 };
        if (t.StartsWith('@'))
        {
            var r = t.Substring(1).TrimStart('+', '*');
            string? pkg = null;
            int colon = r.IndexOf(':');
            if (colon >= 0) { pkg = r.Substring(0, colon); r = r.Substring(colon + 1); }
            uint? id = pkg == "android" ? ManifestAttrs.Framework(r) : resolveRef(r);
            if (id == null) { err = $"recurso {t} nao existe no modelo"; return null; }
            return new Value { Type = TypeReferenceV, Data = id.Value };
        }
        if ((def.Mask & ManifestAttrs.TypeColor) != 0 && t.StartsWith('#') && TryColor(t, out var cv)) return cv;
        if ((def.Mask & ManifestAttrs.TypeBoolean) != 0)
        {
            if (t is "true" or "TRUE" or "True") return new Value { Type = TypeIntBoolean, Data = 0xFFFFFFFF };
            if (t is "false" or "FALSE" or "False") return new Value { Type = TypeIntBoolean, Data = 0 };
        }
        if ((def.Mask & ManifestAttrs.TypeInteger) != 0 && TryInt(t, out var iv)) return iv;
        if ((def.Mask & ManifestAttrs.TypeFloat) != 0 &&
            float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
            return new Value { Type = TypeFloatV, Data = BitConverter.SingleToUInt32Bits(f) };
        if ((def.Mask & ManifestAttrs.TypeString) != 0) return new Value { Type = TypeStringV, Str = v };
        err = $"valor invalido para o formato 0x{def.Mask:x}";
        return null;
    }

    static bool TryInt(string t, out Value v)
    {
        v = new Value();
        bool neg = t.StartsWith('-');
        var s = neg ? t.Substring(1) : t;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!uint.TryParse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h)) return false;
            v.Type = TypeIntHex; v.Data = neg ? unchecked((uint)-(long)h) : h;
            return true;
        }
        if (!long.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var d) ||
            d < int.MinValue || d > uint.MaxValue) return false;
        v.Type = TypeIntDec; v.Data = unchecked((uint)d);
        return true;
    }

    static bool TryColor(string t, out Value v)
    {
        v = new Value();
        var h = t.Substring(1);
        if (!uint.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var n)) return false;
        uint Expand4(uint x) => (x << 4) | x;
        switch (h.Length)
        {
            case 3:
                v.Type = TypeRgb4;
                v.Data = 0xFF000000 | (Expand4((n >> 8) & 0xF) << 16) | (Expand4((n >> 4) & 0xF) << 8) | Expand4(n & 0xF);
                return true;
            case 4:
                v.Type = TypeArgb4;
                v.Data = (Expand4((n >> 12) & 0xF) << 24) | (Expand4((n >> 8) & 0xF) << 16) | (Expand4((n >> 4) & 0xF) << 8) | Expand4(n & 0xF);
                return true;
            case 6: v.Type = TypeRgb8; v.Data = 0xFF000000 | n; return true;
            case 8: v.Type = TypeArgb8; v.Data = n; return true;
        }
        return false;
    }
}

/// <summary>ResStringPool: codifica/decodifica pools UTF-8 e UTF-16.</summary>
static class StringPool
{
    public const uint Utf8Flag = 0x100, SortedFlag = 0x1;

    public static byte[] EncodeUtf16(IReadOnlyList<string> strings) => Encode(strings, utf8: false, Array.Empty<uint>(), Array.Empty<byte>());

    /// <summary>Monta um chunk RES_STRING_POOL_TYPE; estilos (offsets + dados) sao copiados como vieram.</summary>
    public static byte[] Encode(IReadOnlyList<string> strings, bool utf8, uint[] styleOffsets, byte[] styleData)
    {
        var data = new MemoryStream();
        var offsets = new uint[strings.Count];
        for (int i = 0; i < strings.Count; i++)
        {
            offsets[i] = (uint)data.Length;
            var s = strings[i];
            if (utf8)
            {
                var b = Encoding.UTF8.GetBytes(s);
                WriteLen8(data, s.Length);
                WriteLen8(data, b.Length);
                data.Write(b);
                data.WriteByte(0);
            }
            else
            {
                WriteLen16(data, s.Length);
                var b = Encoding.Unicode.GetBytes(s);
                data.Write(b);
                data.WriteByte(0); data.WriteByte(0);
            }
        }
        while (data.Length % 4 != 0) data.WriteByte(0);
        const int header = 28;
        uint stringsStart = (uint)(header + 4 * strings.Count + 4 * styleOffsets.Length);
        uint stylesStart = styleOffsets.Length > 0 ? stringsStart + (uint)data.Length : 0;
        uint size = stringsStart + (uint)data.Length + (uint)styleData.Length;
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write((ushort)0x0001); w.Write((ushort)header); w.Write(size);
        w.Write((uint)strings.Count); w.Write((uint)styleOffsets.Length);
        w.Write(utf8 ? Utf8Flag : 0u);
        w.Write(stringsStart); w.Write(stylesStart);
        foreach (var o in offsets) w.Write(o);
        foreach (var o in styleOffsets) w.Write(o);
        w.Write(data.ToArray());
        w.Write(styleData);
        w.Flush();
        return ms.ToArray();
    }

    static void WriteLen8(Stream s, int n)
    {
        if (n > 0x7FFF) throw new InvalidDataException("string longa demais para o pool UTF-8");
        if (n > 0x7F) { s.WriteByte((byte)(0x80 | (n >> 8))); s.WriteByte((byte)(n & 0xFF)); }
        else s.WriteByte((byte)n);
    }

    static void WriteLen16(Stream s, int n)
    {
        if (n > 0x7FFF)
        {
            ushort hi = (ushort)(0x8000 | (n >> 16)), lo = (ushort)(n & 0xFFFF);
            s.Write(BitConverter.GetBytes(hi)); s.Write(BitConverter.GetBytes(lo));
        }
        else s.Write(BitConverter.GetBytes((ushort)n));
    }

    public sealed record Parsed(List<string> Strings, bool Utf8, uint[] StyleOffsets, byte[] StyleData, int ChunkSize);

    public static Parsed Decode(ReadOnlySpan<byte> chunk)
    {
        ushort type = BitConverter.ToUInt16(chunk);
        ushort headerSize = BitConverter.ToUInt16(chunk.Slice(2));
        int size = (int)BitConverter.ToUInt32(chunk.Slice(4));
        if (type != 0x0001) throw new InvalidDataException("nao e um string pool");
        int count = (int)BitConverter.ToUInt32(chunk.Slice(8));
        int styleCount = (int)BitConverter.ToUInt32(chunk.Slice(12));
        uint flags = BitConverter.ToUInt32(chunk.Slice(16));
        int stringsStart = (int)BitConverter.ToUInt32(chunk.Slice(20));
        int stylesStart = (int)BitConverter.ToUInt32(chunk.Slice(24));
        bool utf8 = (flags & Utf8Flag) != 0;
        var list = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            int off = stringsStart + (int)BitConverter.ToUInt32(chunk.Slice(headerSize + 4 * i));
            if (utf8)
            {
                int p = off;
                ReadLen8(chunk, ref p); // tamanho em UTF-16 (ignorado)
                int blen = ReadLen8(chunk, ref p);
                list.Add(Encoding.UTF8.GetString(chunk.Slice(p, blen)));
            }
            else
            {
                int p = off;
                int n = BitConverter.ToUInt16(chunk.Slice(p)); p += 2;
                if ((n & 0x8000) != 0) { n = ((n & 0x7FFF) << 16) | BitConverter.ToUInt16(chunk.Slice(p)); p += 2; }
                list.Add(Encoding.Unicode.GetString(chunk.Slice(p, n * 2)));
            }
        }
        var styleOffsets = new uint[styleCount];
        for (int i = 0; i < styleCount; i++)
            styleOffsets[i] = BitConverter.ToUInt32(chunk.Slice(headerSize + 4 * count + 4 * i));
        var styleData = styleCount > 0 && stylesStart > 0 ? chunk.Slice(stylesStart, size - stylesStart).ToArray() : Array.Empty<byte>();
        return new Parsed(list, utf8, styleOffsets, styleData, size);
    }

    static int ReadLen8(ReadOnlySpan<byte> b, ref int p)
    {
        int n = b[p++];
        if ((n & 0x80) != 0) n = ((n & 0x7F) << 8) | b[p++];
        return n;
    }
}

/// <summary>resources.arsc: troca strings do pool global (ex.: o nome do app no modelo).</summary>
static class Arsc
{
    public static byte[] ReplaceGlobalStrings(byte[] arsc, IReadOnlyDictionary<string, string> replacements, out int replaced)
    {
        replaced = 0;
        ushort type = BitConverter.ToUInt16(arsc, 0);
        ushort headerSize = BitConverter.ToUInt16(arsc, 2);
        uint size = BitConverter.ToUInt32(arsc, 4);
        if (type != 0x0002 || size != arsc.Length) throw new InvalidDataException("resources.arsc invalido");
        var pool = StringPool.Decode(arsc.AsSpan(headerSize));
        for (int i = 0; i < pool.Strings.Count; i++)
            if (replacements.TryGetValue(pool.Strings[i], out var r)) { pool.Strings[i] = r; replaced++; }
        var newPool = StringPool.Encode(pool.Strings, pool.Utf8, pool.StyleOffsets, pool.StyleData);
        int restStart = headerSize + pool.ChunkSize;
        var outArr = new byte[headerSize + newPool.Length + (arsc.Length - restStart)];
        Buffer.BlockCopy(arsc, 0, outArr, 0, headerSize);
        Buffer.BlockCopy(newPool, 0, outArr, headerSize, newPool.Length);
        Buffer.BlockCopy(arsc, restStart, outArr, headerSize + newPool.Length, arsc.Length - restStart);
        BitConverter.GetBytes((uint)outArr.Length).CopyTo(outArr, 4);
        return outArr;
    }
}
