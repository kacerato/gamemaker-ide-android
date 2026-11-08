# Verifica no Windows a assinatura Authenticode das ferramentas de build do runtime que o
# app Android executa e grava o SHA-256 de cada uma em gmide-runtime-integrity.json.
# Mesmo modelo de make-integrity-manifest.ps1 (IDE): o app confere os hashes
# (host/RuntimeIntegrity.cs) no lugar do WinTrust.VerifySecurity da IDE, que depende de
# wintrust.dll/X509Certificate.CreateFromSignedFile (inexistentes no Android).
#
# Regras iguais as do YoYoStudio.Plugins.Build.WinTrust.VerifySecurity original:
#  - assinatura Authenticode valida (WinVerifyTrust);
#  - thumbprint do certificado numa das 7 listadas no Build.dll;
#  - O= do assunto igual a "YoYo Games Ltd" ou "YOYO GAMES LIMITED".
# A IDE so verifica o Igor; aqui tambem entram o GMAssetCompiler.dll e o GMWebServer.dll,
# que o BuildBridge carrega no processo :build (no PC eles rodam dentro do Igor/webserver).
#
# Uso: powershell -File tools\make-runtime-manifest.ps1 `
#        -RuntimeDir "C:\ProgramData\GameMakerStudio2-LTS2026\Cache\runtimes\runtime-2026.0.0.23" `
#        -Out gmide-runtime-integrity.json
param(
  [Parameter(Mandatory = $true)][string]$RuntimeDir,
  [Parameter(Mandatory = $true)][string]$Out
)
$ErrorActionPreference = 'Stop'
$files = @(
  'bin/igor/windows/x64/Igor.exe',                    # ${igor_path} verificado pela IDE
  'bin/assetcompiler/windows/x64/GMAssetCompiler.dll',
  'bin/webserver/windows/x64/GMWebServer.dll'
)
$orgs = 'YoYo Games Ltd', 'YOYO GAMES LIMITED'
# Lista de WinTrust.VerifySecurity (Build.dll, LTS2026)
$thumbprints = @(
  '7CE290ABC8B8B80265808A24FD44BE7F909F41BA', '8CC55A9A6ADEEE599B7ACCC73BA9D7BA192D6A17',
  'B1EBF68EFDAB3166872C8782C834BF1943D01C17', 'EC5D35001D12F11ADCF67A6644573466E3557FCA',
  'E043931D1585F946463A65E5703933696245E35F', 'FDC8E872D6EA858B6D533A2E557D334154AD55CF',
  'A69F57C54107F4258DF8A900A9DE05F1A73BBEC3'
)
$entries = [ordered]@{}
foreach ($rel in $files) {
  $path = Join-Path $RuntimeDir ($rel -replace '/', '\')
  if (-not (Test-Path $path)) { throw "$rel nao existe em $RuntimeDir" }
  $sig = Get-AuthenticodeSignature -FilePath $path
  if ($sig.Status -ne 'Valid') { throw "${rel}: assinatura $($sig.Status)" }
  $cert = $sig.SignerCertificate
  if ($thumbprints -notcontains $cert.Thumbprint.ToUpperInvariant()) {
    throw "${rel}: certificado $($cert.Thumbprint) fora da lista do WinTrust"
  }
  $org = ($cert.Subject -split ',') | ForEach-Object { $_.Trim() } | Where-Object { $_ -like 'O=*' } |
    ForEach-Object { $_.Substring(2).TrimEnd('.') } | Select-Object -First 1
  if ($orgs -cnotcontains $org) { throw "${rel}: assinado por '$($cert.Subject)', nao pela YoYo" }
  $entries[$rel] = [ordered]@{
    sha256     = (Get-FileHash -Algorithm SHA256 -Path $path).Hash
    size       = (Get-Item $path).Length
    org        = $org
    thumbprint = $cert.Thumbprint.ToUpperInvariant()
  }
  Write-Host "ok  $rel  $($cert.Subject)"
}
$manifest = [ordered]@{
  format  = 1
  runtime = Split-Path -Leaf $RuntimeDir
  files   = $entries
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content -Encoding UTF8 -Path $Out
Write-Host "manifesto: $Out"
