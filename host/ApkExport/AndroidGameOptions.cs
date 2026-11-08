using System.Collections;
using System.Reflection;

namespace GmIde;

// Game Options sao recursos de project.Options, nao do indice GetResourcesByType.
internal static class AndroidGameOptions
{
    const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    internal static object? Get(object project, bool create = false)
    {
        var assembly = project.GetType().Assembly;
        var optionType = assembly.GetType("YoYoStudio.Resources.GMAndroidOptions", true)!;
        var options = (IEnumerable)(project.GetType().GetField("Options", Any)?.GetValue(project)
            ?? project.GetType().GetProperty("Options", Any)?.GetValue(project)
            ?? throw new MissingMemberException("GMProject.Options"));
        var current = options.Cast<object>().FirstOrDefault(optionType.IsInstanceOfType);
        if (current != null || !create) return current;
        var factory = assembly.GetType("YoYoStudio.Resources.ResourceActivator", true)!
            .GetMethod("CreateInstance", new[] { typeof(Type), typeof(object[]) })!;
        var added = factory.Invoke(null, new object?[] { optionType, null }) ?? throw new InvalidOperationException("Nao foi possivel criar as opcoes Android.");
        var register = project.GetType().GetMethods(Any).Single(m => m.Name == "AddResourceToStorage" && m.GetParameters().Length == 3);
        if (register.Invoke(project, new[] { added, (object)false, true }) is not true)
            throw new InvalidOperationException("Nao foi possivel registrar as opcoes Android no projeto.");
        return added;
    }

    /// <summary>
    /// Build Settings Android vazios (Compile/Target/Minimum SDK, Support Lib). No PC a IDE os preenche
    /// pelo SDK instalado (AndroidBuildToolsVersionGadget.SetDefaults, Build.dll) e recusa o build se
    /// continuarem vazios (Common.CheckAndroid...: "android_build_settings_invalid"); sem isso o Igor
    /// morre em int.Parse("") (AndroidUtils.CreateAndroidEnvironment). No aparelho nao ha SDK: o
    /// "SDK mais recente" e o compileSdk do modelo do runner. Mesma regra do gadget; so campos vazios.
    /// Devolve o que foi preenchido.
    /// </summary>
    internal static List<string> FillMissingBuildSettings(object options, int latestSdk)
    {
        string? Get(string p) => options.GetType().GetProperty(p, Any)?.GetValue(options) as string;
        var filled = new List<string>();
        void Fill(string p, string value)
        {
            if (!string.IsNullOrWhiteSpace(Get(p))) return;
            options.GetType().GetProperty(p, Any)!.SetValue(options, value);
            filled.Add($"{p}={value}");
        }
        // AndroidBuildToolsVersionGadget: num = SDK mais recente; target = compile = num;
        // support lib = "<num>.0.0" (>= 24); minimum = 21 a partir do SDK 28.
        int num = latestSdk;
        Fill("option_android_compile_sdk", Math.Max(num, 21).ToString());
        Fill("option_android_target_sdk", num.ToString());
        Fill("option_android_support_lib", num >= 24 ? num + ".0.0" : "23.1.1");
        Fill("option_android_minimum_sdk", (num < 26 ? 9 : num < 28 ? 14 : 21).ToString());
        return filled;
    }
}
