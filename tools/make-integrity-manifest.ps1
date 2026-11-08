# Verifica no Windows a assinatura Authenticode das DLLs que a IDE checa em
# SecurityChecks.VerifyDLLs e grava o SHA-256 de cada uma em ide-integrity.json.
# O lancador Android confere esses hashes (host/IntegrityCheck.cs).
# Uso: powershell -File tools\make-integrity-manifest.ps1 -GmDir "C:\Program Files\GameMaker-LTS2026" -Out ide-integrity.json
param(
  [Parameter(Mandatory = $true)][string]$GmDir,
  [Parameter(Mandatory = $true)][string]$Out
)
$ErrorActionPreference = 'Stop'
# Mesma lista e mesmos assinantes aceitos pelo VerifyDLLs original.
$names = 'Graphics', 'IDE', 'OSCore', 'SharpFont', 'Utils', 'YYCodeEditor'
$signers = 'YoYo Games Ltd', 'YOYO GAMES LIMITED'
$manifest = [ordered]@{}
foreach ($n in $names) {
  $path = Join-Path $GmDir "$n.dll"
  $sig = Get-AuthenticodeSignature -FilePath $path
  if ($sig.Status -ne 'Valid') { throw "$n.dll: assinatura $($sig.Status)" }
  $subject = $sig.SignerCertificate.Subject
  if (-not ($signers | Where-Object { $subject -like "*O=$_*" -or $subject -like "*CN=$_*" })) {
    throw "$n.dll: assinada por '$subject', nao pela YoYo"
  }
  $manifest["$n.dll"] = (Get-FileHash -Algorithm SHA256 -Path $path).Hash
  Write-Host "ok  $n.dll  $subject"
}
$manifest | ConvertTo-Json | Set-Content -Encoding UTF8 -Path $Out
Write-Host "manifesto: $Out"
