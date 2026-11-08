using GmIde;

// Uso: apk-test <moduloGradle> <template.apk> <template.json> <saida.apk> [pacote] [nome]
// Monta um APK como o app faria no aparelho, com uma chave de teste gerada na hora.
if (args.Length < 4) { Console.Error.WriteLine("uso: apk-test <modulo> <template.apk> <template.json> <saida.apk> [pacote] [nome]"); return 2; }
var tpl = ApkTemplateInfo.Load(args[2]);
var key = RsaSigningKey.CreateSelfSigned("gmide apk-test");
var sw = System.Diagnostics.Stopwatch.StartNew();
ApkBuilder.Build(new ApkBuildRequest
{
    ModuleDir = args[0], TemplateApk = args[1], Template = tpl, OutApk = args[3], Key = key,
    Overrides = new ApkOverrides { PackageName = args.Length > 4 ? args[4] : null, DisplayName = args.Length > 5 ? args[5] : null },
    Log = Console.WriteLine,
});
Console.WriteLine($"ok em {sw.ElapsedMilliseconds} ms");
return 0;
