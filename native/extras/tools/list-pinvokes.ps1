# Lists every P/Invoke (ImplMap) in the given assemblies: Assembly<TAB>Module<TAB>EntryPoint<TAB>Type::Method
param([string]$Dir = 'C:\Program Files\GameMaker-LTS2026', [string]$Filter = '*.dll')
Add-Type -AssemblyName System.Reflection.Metadata
foreach ($f in Get-ChildItem -Recurse -Path $Dir -Filter $Filter) {
  try {
    $fs = [IO.File]::OpenRead($f.FullName)
    $pe = [System.Reflection.PortableExecutable.PEReader]::new($fs)
    if (-not $pe.HasMetadata) { $pe.Dispose(); continue }
    $md = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
    foreach ($th in $md.TypeDefinitions) {
      $t = $md.GetTypeDefinition($th)
      $tn = $md.GetString($t.Namespace) + '.' + $md.GetString($t.Name)
      foreach ($mh in $t.GetMethods()) {
        $m = $md.GetMethodDefinition($mh)
        $imp = $m.GetImport()
        if ($imp.Module.IsNil) { continue }
        $mod = $md.GetString($md.GetModuleReference($imp.Module).Name)
        $ep = $md.GetString($imp.Name); if (-not $ep) { $ep = $md.GetString($m.Name) }
        "{0}`t{1}`t{2}`t{3}::{4}" -f $f.FullName.Substring($Dir.Length+1), $mod, $ep, $tn, $md.GetString($m.Name)
      }
    }
    $pe.Dispose()
  } catch { } finally { if ($fs) { $fs.Dispose() } }
}
