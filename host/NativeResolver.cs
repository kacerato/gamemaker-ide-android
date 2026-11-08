using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Android.Util;

namespace GmIde;

// Traduz os nomes de bibliotecas nativas que a IDE usa no Windows/Linux
// ("Magick.Native-Q8-arm64.dll", "libc.so.6", "freetype.dll" ...) para os .so
// que vao no APK (lib/arm64-v8a) ou no sistema Android.
//
// Ordem no Mono (.NET 8): override do monodroid -> DllImportResolver do assembly
// (registrado aqui para cada assembly da IDE) -> busca padrao -> evento
// AssemblyLoadContext.ResolvingUnmanagedDll (tambem tratado aqui, e onde
// logamos o que ninguem conseguiu resolver).
static class NativeResolver
{
    const string Tag = "GmIde.Native";

    // nome pedido (sem pasta, sem diferenca de maiusculas) -> .so no Android
    static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["libc"] = "libc.so",
        ["libc.so.6"] = "libc.so",
        ["libc.dylib"] = "libc.so",
        ["liblog"] = "liblog.so",
        ["libdl"] = "libdl.so",
        ["libdl.so.2"] = "libdl.so",
        ["libdl.dylib"] = "libdl.so",

        ["Magick.Native-Q8-arm64.dll"] = "libMagick.Native-Q8-arm64.so",
        ["Magick.Native-Q8-x64.dll"] = "libMagick.Native-Q8-arm64.so",
        ["Magick.Native-Q8-x86.dll"] = "libMagick.Native-Q8-arm64.so",
        ["Magick.Native-Q8-arm64"] = "libMagick.Native-Q8-arm64.so",
        ["Magick.Native-Q8-x64"] = "libMagick.Native-Q8-arm64.so",

        ["SDL2"] = "libSDL2.so",
        ["SDL2.dll"] = "libSDL2.so",
        ["libSDL2-2.0.so.0"] = "libSDL2.so",
        ["FNA3D"] = "libFNA3D.so",
        ["FNA3D.dll"] = "libFNA3D.so",
        ["libFNA3D.so.0"] = "libFNA3D.so",
        ["freetype"] = "libfreetype.so",
        ["freetype.dll"] = "libfreetype.so",
        ["freetype.so"] = "libfreetype.so",
        ["libfreetype.so.6"] = "libfreetype.so",
        ["onig"] = "libonig.so",
        ["onig.dll"] = "libonig.so",
        ["yoga"] = "libyoga.so",
        ["yoga.dll"] = "libyoga.so",
        ["yyal"] = "libyyal.so",
        ["yyal.dll"] = "libyyal.so",
        // Reimplementacao Android em native/extras (os dialogos reais sao os do AndroidFileDialogs).
        ["tinyfiledialogs"] = "libtinyfiledialogs.so",
        ["libTinyFileDialogs.so"] = "libtinyfiledialogs.so",
        // native/extras
        ["libHarfBuzzSharp"] = "libHarfBuzzSharp.so",
        ["libHarfBuzzSharp.dll"] = "libHarfBuzzSharp.so",
        ["IDENativeLib"] = "libIDENativeLib.so",
        ["IDENativeLib.dll"] = "libIDENativeLib.so",
    };

    static readonly ConcurrentDictionary<string, IntPtr> Loaded = new(StringComparer.OrdinalIgnoreCase);
    static readonly ConcurrentDictionary<string, byte> Reported = new(StringComparer.OrdinalIgnoreCase);
    static readonly ConcurrentDictionary<Assembly, byte> Hooked = new();
    static string _nativeLibDir = "";
    static string _ideDir = "";
    static bool _installed;

    public static void Install(string ideDir)
    {
        if (_installed) return;
        _installed = true;
        _ideDir = Path.GetFullPath(ideDir);
        _nativeLibDir = MainApplication.Instance.ApplicationInfo?.NativeLibraryDir ?? "";
        Log.Info(Tag, $"Resolver nativo instalado; libs do APK em '{_nativeLibDir}'");
        try
        {
            var present = Directory.Exists(_nativeLibDir)
                ? string.Join(", ", Directory.GetFiles(_nativeLibDir, "*.so").Select(Path.GetFileName))
                : "(pasta nao existe)";
            Log.Info(Tag, "Libs no APK: " + present);
        }
        catch (Exception e) { Log.Warn(Tag, "Nao listei as libs do APK: " + e.Message); }

        AssemblyLoadContext.Default.ResolvingUnmanagedDll += OnResolvingUnmanagedDll;
        AppDomain.CurrentDomain.AssemblyLoad += (_, a) => Hook(a.LoadedAssembly);
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            Hook(asm);
    }

    static bool ShouldHook(Assembly asm)
    {
        if (asm == typeof(NativeResolver).Assembly) return true;
        if (asm.IsDynamic) return false;
        string loc;
        try { loc = asm.Location; } catch { return false; }
        return !string.IsNullOrEmpty(loc) &&
               Path.GetFullPath(loc).StartsWith(_ideDir, StringComparison.OrdinalIgnoreCase);
    }

    static void Hook(Assembly asm)
    {
        if (!ShouldHook(asm) || !Hooked.TryAdd(asm, 0)) return;
        try
        {
            NativeLibrary.SetDllImportResolver(asm, Resolve);
        }
        catch (Exception e)
        {
            // Ja existe um resolver no assembly (ex.: definido pela propria biblioteca).
            Log.Warn(Tag, $"SetDllImportResolver({asm.GetName().Name}) falhou: {e.GetType().Name}: {e.Message}");
        }
    }

    static IntPtr Resolve(string libraryName, Assembly asm, DllImportSearchPath? searchPath)
    {
        return TryMapped(libraryName, asm);
    }

    static IntPtr OnResolvingUnmanagedDll(Assembly asm, string libraryName)
    {
        var h = TryMapped(libraryName, asm);
        if (h != IntPtr.Zero) return h;
        if (Reported.TryAdd("miss:" + libraryName, 0))
            Log.Warn(Tag, $"NAO resolvido: '{libraryName}' (pedido por {asm?.GetName().Name}). " +
                          "Chamadas P/Invoke para essa lib vao dar DllNotFoundException.");
        return IntPtr.Zero;
    }

    /// <summary>Para AssemblyLoadContexts proprios (BuildBridge.ToolLoadContext).</summary>
    internal static IntPtr TryMap(string libraryName, Assembly? asm) => TryMapped(libraryName, asm);

    static IntPtr TryMapped(string libraryName, Assembly? asm)
    {
        var key = Path.GetFileName(libraryName.Replace('\\', '/'));
        if (!Map.TryGetValue(key, out var target)) return IntPtr.Zero;
        if (Loaded.TryGetValue(target, out var cached))
        {
            if (Reported.TryAdd("map:" + libraryName, 0))
                Log.Info(Tag, $"'{libraryName}' ({asm?.GetName().Name}) -> {target} (ja carregada)");
            return cached;
        }

        var candidates = new List<string>();
        if (_nativeLibDir.Length > 0) candidates.Add(Path.Combine(_nativeLibDir, target));
        candidates.Add(target); // bibliotecas do sistema (libc.so, liblog.so, libdl.so) e namespace do app
        var errors = new List<string>();
        foreach (var c in candidates)
        {
            // Pula caminho absoluto inexistente (o erro util e o do dlopen de um arquivo que existe).
            if (Path.IsPathRooted(c) && !File.Exists(c)) { errors.Add(c + ": arquivo nao existe"); continue; }
            try
            {
                var h = NativeLibrary.Load(c);
                Loaded[target] = h;
                if (Reported.TryAdd("map:" + libraryName, 0))
                    Log.Info(Tag, $"'{libraryName}' ({asm?.GetName().Name}) -> {c}");
                return h;
            }
            catch (Exception e)
            {
                // A mensagem costuma trazer o dlerror (ex.: dependencia .so faltando).
                errors.Add(c + ": " + e.Message);
            }
        }
        if (Reported.TryAdd("fail:" + libraryName, 0))
            Log.Warn(Tag, $"'{libraryName}' ({asm?.GetName().Name}) mapeado para {target}, mas o carregamento falhou: " +
                          string.Join(" | ", errors));
        return IntPtr.Zero;
    }
}
