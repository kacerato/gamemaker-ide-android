using System.Reflection;
using System.Runtime.InteropServices;
using Android.Util;

namespace GmIde;

// A IDE usa Assembly.GetEntryAssembly() (versao e pasta do executavel), que no
// Android e null. O .NET 8 nao tem Assembly.SetEntryAssembly (so o .NET 9), entao:
//  1. se o CoreLib tiver um campo estatico "forcado" de entry assembly, preenchemos;
//  2. tambem avisamos o runtime Mono via API de embedding (mono_assembly_set_main).
static class MonoHacks
{
    const string Tag = "GmIde";

    [DllImport("libmonosgen-2.0.so")]
    static extern void mono_assembly_set_main(IntPtr monoAssembly);

    public static void SetEntryAssembly(Assembly asm)
    {
        // 1. Campo estatico do CoreLib (ex.: s_forceEntryAssembly)
        foreach (var f in typeof(Assembly).GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
        {
            // No .NET 8 Mono o campo e "s_overriddenEntryAssembly", tipado como object.
            if ((f.FieldType == typeof(Assembly) || f.FieldType == typeof(object)) &&
                f.Name.Contains("EntryAssembly", StringComparison.OrdinalIgnoreCase))
            {
                f.SetValue(null, asm);
                Log.Info(Tag, $"Entry assembly via campo {f.Name}");
            }
        }

        // 2. Runtime Mono
        var field = asm.GetType().GetField("_mono_assembly", BindingFlags.Instance | BindingFlags.NonPublic);
        if (field?.GetValue(asm) is IntPtr ptr && ptr != IntPtr.Zero)
        {
            mono_assembly_set_main(ptr);
            Log.Info(Tag, "mono_assembly_set_main chamado");
        }
        else
        {
            Log.Warn(Tag, "Campo _mono_assembly nao encontrado em " + asm.GetType().FullName);
        }

        var e = Assembly.GetEntryAssembly();
        Log.Info(Tag, $"GetEntryAssembly() = {(e == null ? "null" : e.FullName + " @ " + e.Location)}");
        if (e == null)
        {
            var names = string.Join(", ", typeof(Assembly).GetFields(BindingFlags.Static | BindingFlags.NonPublic)
                .Select(f => f.Name + ":" + f.FieldType.Name));
            Log.Warn(Tag, "Campos estaticos de Assembly: " + names);
        }
    }
}
