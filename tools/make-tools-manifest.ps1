# Verifica no Windows as ferramentas externas da IDE que o app Android roda DENTRO do
# processo :build (host/ToolBridge.cs) e grava o SHA-256 de cada arquivo em
# gmide-tools-integrity.json. Mesmo modelo de make-runtime-manifest.ps1 e
# make-integrity-manifest.ps1: o PC verifica a assinatura, o app confere o hash.
#
#  - Language Server (CodeEditor2/Servers/GML.lspConfig -> GameMakerLanguageServer):
#    GameMakerLanguageServer.dll precisa ter Authenticode valido, thumbprint na lista do
#    WinTrust.VerifySecurity (Build.dll) e O= YoYo (as mesmas regras do Igor). Entram tambem
#    todas as DLLs da pasta da IDE que ele carrega (fechamento das referencias de assembly,
#    exceto o framework). Cada uma precisa ter assinatura Authenticode valida (YoYo,
#    Microsoft, .NET Foundation...); o app recusa carregar qualquer DLL fora do manifesto.
#  - ProjectTool (packages/gm-tools/project-tool-win-x64/ProjectTool.exe): o ProjectTool.exe
#    da LTS2026 NAO e assinado (Get-AuthenticodeSignature = NotSigned) e a IDE original o
#    executa sem verificar nada. Aqui ele entra com "signed": false e o hash amarra a copia do
#    aparelho a copia instalada no PC. Se um dia vier assinado, a assinatura passa a ser exigida
#    (thumbprint/O= iguais aos do Igor). Os dados de Formats/ e o package.json vao junto.
#
# Uso: powershell -File tools\make-tools-manifest.ps1 `
#        -GmDir "C:\Program Files\GameMaker-LTS2026" -Out gmide-tools-integrity.json
param(
  [Parameter(Mandatory = $true)][string]$GmDir,
  [Parameter(Mandatory = $true)][string]$Out
)
$ErrorActionPreference = 'Stop'
$orgs = 'YoYo Games Ltd', 'YOYO GAMES LIMITED'
$thumbprints = @(
  '7CE290ABC8B8B80265808A24FD44BE7F909F41BA', '8CC55A9A6ADEEE599B7ACCC73BA9D7BA192D6A17',
  'B1EBF68EFDAB3166872C8782C834BF1943D01C17', 'EC5D35001D12F11ADCF67A6644573466E3557FCA',
  'E043931D1585F946463A65E5703933696245E35F', 'FDC8E872D6EA858B6D533A2E557D334154AD55CF',
  'A69F57C54107F4258DF8A900A9DE05F1A73BBEC3'
)
$ptDir = 'packages/gm-tools/project-tool-win-x64'
$lspMain = 'GameMakerLanguageServer.dll'

function OrgOf($cert) {
  ($cert.Subject -split ',') | ForEach-Object { $_.Trim() } | Where-Object { $_ -like 'O=*' } |
    ForEach-Object { $_.Substring(2).Trim('"').TrimEnd('.') } | Select-Object -First 1
}

function Entry([string]$rel, [string]$mode) {
  # mode: yoyo = assinatura YoYo obrigatoria; signed = qualquer assinatura valida;
  #       data = arquivo de dados (so hash); unsignedtool = executavel nao assinado no original
  $path = Join-Path $GmDir ($rel -replace '/', '\')
  if (-not (Test-Path -LiteralPath $path)) { throw "$rel nao existe em $GmDir" }
  $e = [ordered]@{
    sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash
    size   = (Get-Item -LiteralPath $path).Length
  }
  if ($mode -eq 'data') { $e.signed = $false; return $e }
  $sig = Get-AuthenticodeSignature -LiteralPath $path
  if ($mode -eq 'unsignedtool' -and $sig.Status -eq 'NotSigned') {
    $e.signed = $false
    Write-Host "ok  $rel  (nao assinado no original; so hash)"
    return $e
  }
  if ($sig.Status -ne 'Valid') { throw "${rel}: assinatura $($sig.Status)" }
  $cert = $sig.SignerCertificate
  $org = OrgOf $cert
  if ($mode -in 'yoyo', 'unsignedtool') {
    if ($thumbprints -notcontains $cert.Thumbprint.ToUpperInvariant()) { throw "${rel}: certificado $($cert.Thumbprint) fora da lista do WinTrust" }
    if ($orgs -cnotcontains $org) { throw "${rel}: assinado por '$($cert.Subject)', nao pela YoYo" }
  }
  $e.signed = $true
  $e.org = $org
  $e.thumbprint = $cert.Thumbprint.ToUpperInvariant()
  Write-Host "ok  $rel  $org"
  return $e
}

# Fechamento das referencias do LSP dentro da pasta da IDE: System.Reflection.Metadata no
# PowerShell 7 (pwsh); no Windows PowerShell 5.1 (.NET Framework, sem esse assembly),
# Assembly.ReflectionOnlyLoadFrom(...).GetReferencedAssemblies().
try { Add-Type -AssemblyName System.Reflection.Metadata -ErrorAction Stop } catch { }
$haveSrm = $null -ne ('System.Reflection.Metadata.MetadataReader' -as [type])
function AsmRefs([string]$file) {
  if (-not $haveSrm) {
    try { return [Reflection.Assembly]::ReflectionOnlyLoadFrom($file).GetReferencedAssemblies() | ForEach-Object { $_.Name } }
    catch { return @() }
  }
  $fs = [IO.File]::OpenRead($file)
  try {
    $pe = New-Object System.Reflection.PortableExecutable.PEReader($fs)
    if (-not $pe.HasMetadata) { return @() }
    $md = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
    $md.AssemblyReferences | ForEach-Object { $md.GetString($md.GetAssemblyReference($_).Name) }
  } finally { $fs.Dispose() }
}
$isFramework = { param($n) $n -in 'mscorlib', 'netstandard', 'System', 'Microsoft.CSharp', 'Microsoft.VisualBasic', 'Microsoft.VisualBasic.Core', 'WindowsBase' -or $n -like 'System.*' -or $n -like 'Microsoft.Win32.*' }
$seen = [ordered]@{}
$queue = New-Object System.Collections.Queue
$queue.Enqueue('GameMakerLanguageServer')
while ($queue.Count -gt 0) {
  $n = $queue.Dequeue()
  if ($seen.Contains($n)) { continue }
  $f = Join-Path $GmDir "$n.dll"
  if (-not (Test-Path -LiteralPath $f)) { continue }   # framework do Mono ou ausente
  $seen[$n] = $true
  foreach ($r in AsmRefs $f) {
    # System.* que existe na pasta da IDE (ex.: System.IO.Pipelines) tambem entra: o app
    # tenta o framework primeiro e so carrega da pasta se o Mono nao tiver.
    if (-not $seen.Contains($r) -and ((-not (& $isFramework $r)) -or (Test-Path -LiteralPath (Join-Path $GmDir "$r.dll")))) { $queue.Enqueue($r) }
  }
}

$entries = [ordered]@{}
foreach ($n in $seen.Keys) {
  $rel = "$n.dll"
  $entries[$rel] = Entry $rel ($(if ($rel -eq $lspMain) { 'yoyo' } else { 'signed' }))
}
$entries["$ptDir/ProjectTool.exe"] = Entry "$ptDir/ProjectTool.exe" 'unsignedtool'
Get-ChildItem -LiteralPath (Join-Path $GmDir $ptDir) -Recurse -File | Where-Object { $_.Name -ne 'ProjectTool.exe' } | ForEach-Object {
  $rel = ($_.FullName.Substring($GmDir.TrimEnd('\').Length + 1)) -replace '\\', '/'
  $entries[$rel] = Entry $rel 'data'
}

$manifest = [ordered]@{
  format = 1
  ide    = (Get-Item -LiteralPath (Join-Path $GmDir 'IDE.dll')).VersionInfo.FileVersion
  lsp    = @($seen.Keys | ForEach-Object { "$_.dll" })
  files  = $entries
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 -Path $Out
Write-Host "manifesto: $Out ($($entries.Count) arquivos)"
