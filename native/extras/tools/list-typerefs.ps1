# Lists which assemblies reference members of the given types (MemberRef parent = TypeRef name match)
param([string]$Dir = 'C:\Program Files\GameMaker-LTS2026', [string]$Pattern)
Add-Type -AssemblyName System.Reflection.Metadata
foreach ($f in Get-ChildItem -Path $Dir -Filter *.dll) {
  try {
    $fs = [IO.File]::OpenRead($f.FullName); $pe = [System.Reflection.PortableExecutable.PEReader]::new($fs)
    if (-not $pe.HasMetadata) { continue }
    $md = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
    foreach ($h in $md.MemberReferences) {
      $r = $md.GetMemberReference($h)
      if ($r.Parent.Kind -ne [System.Reflection.Metadata.HandleKind]::TypeReference) { continue }
      $t = $md.GetTypeReference([System.Reflection.Metadata.TypeReferenceHandle]$r.Parent)
      $tn = $md.GetString($t.Namespace) + '.' + $md.GetString($t.Name)
      if ($tn -match $Pattern) { "{0}`t{1}::{2}" -f $f.Name, $tn, $md.GetString($r.Name) }
    }
  } catch {} finally { if ($pe) { $pe.Dispose() }; if ($fs) { $fs.Dispose() } }
}
