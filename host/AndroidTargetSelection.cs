using System.Reflection;

namespace GmIde;

internal enum LocalTargetAction { Original, ExportApk, Unsupported }

internal static class AndroidTargetSelection
{
    internal static string? Tag(object? gadget)
    {
        for (var type = gadget?.GetType(); type != null; type = type.BaseType)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            var value = type.GetField("Tag", flags)?.GetValue(gadget) ?? type.GetProperty("Tag", flags)?.GetValue(gadget);
            if (value != null) return value.ToString();
        }
        return null;
    }

    internal static LocalTargetAction Route(string? clicked, string? platform, string? runtime, string? device)
    {
        bool Is(string? value, string expected) => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
        if (!Is(clicked, "android") && !Is(platform, "android")) return LocalTargetAction.Original;
        if ((!string.IsNullOrEmpty(runtime) && !Is(runtime, "VM")) ||
            (!string.IsNullOrEmpty(device) && !Is(device, "Default"))) return LocalTargetAction.Unsupported;
        return Is(clicked, "android") || Is(clicked, "VM") || Is(clicked, "Default")
            ? LocalTargetAction.ExportApk : LocalTargetAction.Unsupported;
    }
}
