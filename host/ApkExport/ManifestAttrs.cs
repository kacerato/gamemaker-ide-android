namespace GmIde;

/// <summary>Atributo android:* do manifesto: id publico, mascara de formatos (ResTable_map::TYPE_*), enums e flags.</summary>
sealed record Attr(uint Id, uint Mask, Dictionary<string, int>? Enums, Dictionary<string, uint>? Flags);

// Tabela gerada por tools/apk-template/gen-attrs.py a partir do SDK (ManifestAttrs.g.cs).
static partial class ManifestAttrs
{
    public const uint TypeReference = 1u << 0, TypeString = 1u << 1, TypeInteger = 1u << 2, TypeBoolean = 1u << 3,
        TypeColor = 1u << 4, TypeFloat = 1u << 5, TypeDimension = 1u << 6, TypeFraction = 1u << 7,
        TypeEnum = 1u << 16, TypeFlags = 1u << 17;

    static readonly Dictionary<string, Attr> _attrs = Make();
    static readonly Dictionary<string, uint> _framework = MakeFramework();

    static Dictionary<string, Attr> Make() { var d = new Dictionary<string, Attr>(StringComparer.Ordinal); Fill(d); return d; }
    static Dictionary<string, uint> MakeFramework() { var d = new Dictionary<string, uint>(StringComparer.Ordinal); FillFramework(d); return d; }

    public static Attr? Get(string name) => _attrs.TryGetValue(name, out var a) ? a : null;

    /// <summary>"style/Theme.Black" -> id publico do framework (android:...).</summary>
    public static uint? Framework(string typeAndName) => _framework.TryGetValue(typeAndName, out var id) ? id : null;
}
