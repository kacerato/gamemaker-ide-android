using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using System.Runtime.Serialization.Json;
using System.Text.Json.Nodes;
using GmIde;

string work = Path.GetFullPath(args[0]);
Check(AndroidTargetSelection.Route("Default", "android", "VM", "Default") == LocalTargetAction.ExportApk &&
      AndroidTargetSelection.Route("VM", "android", "VM", "Default") == LocalTargetAction.ExportApk &&
      AndroidTargetSelection.Route("android", "android", "VM", "Default") == LocalTargetAction.ExportApk,
      "Android, VM e Default no alvo Android seguem para a exportacao local");
Check(AndroidTargetSelection.Route("Default", "Test", "VM", "Default") == LocalTargetAction.Original &&
      AndroidTargetSelection.Route("Default", "Linux", "VM", "Default") == LocalTargetAction.Original,
      "Default de outros alvos nao e confundido com exportacao Android");
Check(AndroidTargetSelection.Route("YYC", "android", "YYC", "Default") == LocalTargetAction.Unsupported &&
      AndroidTargetSelection.Route("remoto", "android", "VM", "remoto") == LocalTargetAction.Unsupported,
      "YYC e dispositivo remoto recebem estado nao suportado, sem cair no login");
Check(AndroidTargetSelection.Tag(new DerivedTag()) == "Default" && AndroidTargetSelection.Tag(new PropertyTag()) == "VM",
      "Tag aceita campo herdado e propriedade");
Directory.CreateDirectory(work);
string user = Path.Combine(work, "user"), runtime = Path.Combine(work, "runtime");
Directory.CreateDirectory(user); Directory.CreateDirectory(Path.Combine(runtime, "bin"));
string bff = Path.Combine(work, "build.bff"), local = Path.Combine(user, "local_settings.json");
string custom = Path.Combine(work, "configured-key.fixture"), fallback = Path.Combine(work, "default-key.fixture");
File.WriteAllText(custom, "Somente fixture de selecao de caminho, sem teste criptografico.");
File.WriteAllText(bff, new JsonObject { ["userDir"] = user, ["runtimeLocation"] = runtime }.ToJsonString());
const string key = "machine.Platform Settings.Android.Keystore.filename";
File.WriteAllText(Path.Combine(runtime, "bin", "platform_setting_defaults.json"), new JsonObject { [key] = custom }.ToJsonString());
File.Delete(local);
Check(ApkKeystoreSelection.ReadConfiguredPath(bff) == custom, "default do runtime lido pelo caminho correto");
File.WriteAllText(local, new JsonObject { [key] = "ausente.p12" }.ToJsonString());
Check(ApkKeystoreSelection.ReadConfiguredPath(bff) == "ausente.p12", "preferencia local tem precedencia mesmo quando o arquivo falta");
int defaultsCreated = 0;
string DefaultKey() { defaultsCreated++; return fallback; }
try { ApkKeystoreSelection.Resolve(Path.Combine(work, "ausente.p12"), DefaultKey); throw new Exception("Aceitou chave ausente."); }
catch (FileNotFoundException) { Check(defaultsCreated == 0, "chave configurada ausente nao troca a assinatura nem cria chave padrao"); }
Check(ApkKeystoreSelection.Resolve(custom, DefaultKey) == custom && defaultsCreated == 0, "chave configurada existente preservada");
File.WriteAllText(local, new JsonObject { [key] = "" }.ToJsonString());
Check(string.IsNullOrEmpty(ApkKeystoreSelection.ReadConfiguredPath(bff)), "configuracao vazia local sobrepoe o default do runtime");
Check(ApkKeystoreSelection.Resolve(null, DefaultKey) == fallback && defaultsCreated == 1, "chave padrao somente sem configuracao");
Check(ApkKeystoreSelection.SamePath(custom, Path.Combine(work, ".", Path.GetFileName(custom))), "comparacao do caminho mostrado com o usado");
File.WriteAllText(local, "{");
try { ApkKeystoreSelection.ReadConfiguredPath(bff); throw new Exception("Ignorou preferencias corrompidas."); }
catch (System.Text.Json.JsonException) { Console.WriteLine("PASS: preferencias invalidas nao provocam fallback silencioso"); }
catch (InvalidDataException e) when (e.InnerException is System.Text.Json.JsonException)
{ Console.WriteLine("PASS: preferencias invalidas retornam erro contextual sem fallback silencioso"); }

string ide = Path.GetFullPath(args[1]);
using var resolver = new Mono.Cecil.DefaultAssemblyResolver();
resolver.AddSearchDirectory(ide);
using (var module = Mono.Cecil.ModuleDefinition.ReadModule(Path.Combine(ide, "CoreResources.dll"),
    new Mono.Cecil.ReaderParameters { AssemblyResolver = resolver }))
{
    Check(IdePatches.PatchLocalAndroidOptionLoading(module), "carregador real inclui o tipo de opcoes Android antes do filtro de versao");
    module.Write(Path.Combine(work, "CoreResources.dll"));
}
using (var module = Mono.Cecil.ModuleDefinition.ReadModule(Path.Combine(ide, "IDE.dll"),
    new Mono.Cecil.ReaderParameters { AssemblyResolver = resolver }))
{
    Check(IdePatches.PatchLocalAndroidOptions(module), "patch encontra os dois caminhos reais das Game Options no IDE.dll");
    module.Write(Path.Combine(work, "IDE-options-patched.dll"));
}
using (var module = Mono.Cecil.ModuleDefinition.ReadModule(Path.Combine(work, "IDE-options-patched.dll")))
{
    foreach (string name in new[] { "GetLicensedOptions", "VerifyLicensedOptions" })
    {
        var m = module.GetType("YoYoStudio.Resources.GMProjectAdapter").Methods.Single(m => m.Name == name);
        Check(m.Body.Instructions.Count(i => i.OpCode == Mono.Cecil.Cil.OpCodes.Ldstr && Equals(i.Operand, "android")) == 1 &&
              m.Body.Instructions.Any(i => i.Operand is Mono.Cecil.MethodReference call && call.Name == "get_TestPlatform_Actual"),
              name + " inclui Android e preserva a selecao da plataforma de teste");
    }
}
AssemblyLoadContext.Default.Resolving += (context, name) =>
    File.Exists(Path.Combine(ide, name.Name + ".dll")) ? context.LoadFromAssemblyPath(Path.Combine(ide, name.Name + ".dll")) : null;
var core = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(work, "CoreResources.dll"));
AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(ide, "FileIO.dll"))
    .GetType("YoYoStudio.Resources.FileIO", true)!.GetMethod("SetDefaultFileFunctions")!.Invoke(null, null);
object project = Activator.CreateInstance(core.GetType("YoYoStudio.Resources.GMProject", true)!)!;
var licenseSource = DispatchProxy.Create(core.GetType("YoYoStudio.Resources.ILicenseModulesSource", true)!, typeof(EmptyLicenseSource));
project.GetType().GetProperty("LicenseModules")!.SetValue(null, licenseSource);
var loadedOptionTypes = ((IEnumerable)project.GetType().GetMethod("GetOptionTypeNamesList")!.Invoke(null, new object?[] { null })!).Cast<string>().ToList();
Check(loadedOptionTypes.Count(t => t == "GMAndroidOptions") == 1 && loadedOptionTypes.Contains("GMMainOptions"),
    "carregador executado sem modulos de conta inclui Android uma vez e preserva Main");
project.GetType().GetProperty("pathToYYP")!.SetValue(project, Path.Combine(work, "TestGame.yyp"));
Check(AndroidGameOptions.Get(project) == null, "projeto sem opcoes Android reconhecido");
object options = AndroidGameOptions.Get(project, create: true)!;
Check(ReferenceEquals(options, AndroidGameOptions.Get(project, create: true)), "opcoes existentes reutilizadas sem duplicacao");
var list = ((IEnumerable)project.GetType().GetField("Options")!.GetValue(project)!).Cast<object>().ToList();
Check(list.Count == 1 && ReferenceEquals(list[0], options), "opcoes registradas na colecao real do projeto");
var optionType = options.GetType();
optionType.GetProperty("option_android_display_name")!.SetValue(options, "Jogo personalizado");
optionType.GetProperty("option_android_package_company")!.SetValue(options, "equipe.mobile");
optionType.GetProperty("option_android_version")!.SetValue(options, "2.3.4.0");
optionType.GetProperty("option_android_orient_portrait")!.SetValue(options, false);
optionType.GetProperty("option_android_orient_portrait_flipped")!.SetValue(options, false);
optionType.GetProperty("option_android_permission_record_audio")!.SetValue(options, true);
string iconSource = Path.Combine(work, "icon-source.png");
byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRz8AAAAASUVORK5CYII=");
File.WriteAllBytes(iconSource, png);
string ExpandIconPath(string p) => p.Replace("${options_dir}", Path.Combine(work, "options")).Replace('\\', '/');
int rendered = 0;
AndroidProjectIcons.Import(options, iconSource, ExpandIconPath, (_, _) => { rendered++; return png; });
string iconRef = (string)optionType.GetProperty("option_android_icon_xxxhdpi")!.GetValue(options)!;
Check(File.Exists(ExpandIconPath(iconRef)) && !iconRef.Contains("icon-source") && rendered is 0 or 12,
    "importacao copia icones regulares/adaptativos para o projeto, sem depender do arquivo do picker");
AndroidProjectIcons.Import(options, iconSource, ExpandIconPath, (_, _) => throw new Exception("Renderizou de novo"));
Check((string)optionType.GetProperty("option_android_icon_xxxhdpi")!.GetValue(options)! == iconRef,
    "reaplicar o registro pendente reutiliza as imagens do projeto");
Check((bool)optionType.GetProperty("isDirty")!.GetValue(options)!, "edicao marca o recurso para salvar");
using var data = new MemoryStream();
var serializer = new DataContractJsonSerializer(optionType);
serializer.WriteObject(data, options);
File.WriteAllBytes(Path.Combine(work, "android-options-data-contract.json"), data.ToArray());
data.Position = 0;
object restored = serializer.ReadObject(data)!;
Check((string)optionType.GetProperty("option_android_display_name")!.GetValue(restored)! == "Jogo personalizado" &&
      (string)optionType.GetProperty("option_android_package_company")!.GetValue(restored)! == "equipe.mobile" &&
      (string)optionType.GetProperty("option_android_version")!.GetValue(restored)! == "2.3.4.0", "nome, pacote e versao sobrevivem ao round-trip do modelo real");
Check((string)optionType.GetProperty("option_android_icon_xxxhdpi")!.GetValue(restored)! == iconRef &&
      !(bool)optionType.GetProperty("option_android_orient_portrait")!.GetValue(restored)! &&
      !(bool)optionType.GetProperty("option_android_orient_portrait_flipped")!.GetValue(restored)! &&
      (bool)optionType.GetProperty("option_android_permission_record_audio")!.GetValue(restored)!,
      "icone do projeto, orientacao e permissao sobrevivem ao contrato de dados real");
Console.WriteLine("PASS: regressao de selecao da chave e persistencia das opcoes concluida no host.");

static void Check(bool pass, string description)
{
    if (!pass) throw new Exception(description);
    Console.WriteLine("PASS: " + description);
}

class FieldTag { public string Tag = "Default"; }
sealed class DerivedTag : FieldTag { }
sealed class PropertyTag { public string Tag => "VM"; }

public class EmptyLicenseSource : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args) =>
        method?.Name == "get_Modules" ? Array.Empty<string>() : null;
}
