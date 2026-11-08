#!/usr/bin/env bash
# Gera o pacote para instalar o port SEM adb (host/PackageImport.cs):
#   dist/gmide-package.zip   IDE + runtime + Prefabs + manifestos de integridade + package.json
#   dist/io.gmide.android-Signed.apk
#   dist/LEIA-ME.txt
# O usuario copia o zip e o APK para a pasta Download do celular (cabo USB/MTP, nuvem...),
# instala o APK tocando nele e abre o app: no primeiro boot o app extrai o zip.
#
# Layout do zip (caminhos com "/"):
#   ide/...                                        instalacao do GameMaker (C:\Program Files\GameMaker-LTS2026)
#   ide/ide-integrity.json                         tools/make-integrity-manifest.ps1 (Authenticode no PC)
#   runtime/runtime-2026.0.0.23/...                runtime (inclui receipt.json)
#   runtime/runtime-2026.0.0.23/gmide-runtime-integrity.json   tools/make-runtime-manifest.ps1
#   runtime/Prefabs/...
#   package.json                                   versoes + SHA-256 de cada arquivo do zip
#
# Uso (Git Bash): bash tools/make-package.sh
# Variaveis opcionais: GM_DIR, RT_DIR, PREFABS_DIR, APK, DIST
set -euo pipefail
export MSYS_NO_PATHCONV=1
HERE="$(cd "$(dirname "$0")/.." && pwd)"
PKG=io.gmide.android
RT_VER=runtime-2026.0.0.23
GM_DIR="${GM_DIR:-/c/Program Files/GameMaker-LTS2026}"
RT_DIR="${RT_DIR:-/c/ProgramData/GameMakerStudio2-LTS2026/Cache/runtimes/$RT_VER}"
PREFABS_DIR="${PREFABS_DIR:-/c/ProgramData/GameMakerStudio2-LTS2026/Prefabs}"
APK="${APK:-$HERE/host/bin/Release/net8.0-android/android-arm64/$PKG-Signed.apk}"
DIST="${DIST:-$HERE/dist}"

[ -f "$GM_DIR/IDE.dll" ] || { echo "IDE nao encontrada em $GM_DIR"; exit 1; }
[ -f "$RT_DIR/receipt.json" ] || { echo "Runtime nao encontrado em $RT_DIR"; exit 1; }
[ -d "$PREFABS_DIR" ] || { echo "Aviso: $PREFABS_DIR nao existe; pacote sem Prefabs"; PREFABS_DIR=""; }

mkdir -p "$DIST"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
w() { cygpath -w "$1"; }

# 1) Manifestos de integridade: os scripts existentes conferem o Authenticode da YoYo no Windows.
echo "== verificando assinaturas da IDE"
powershell -NoProfile -ExecutionPolicy Bypass -File "$(w "$HERE/tools/make-integrity-manifest.ps1")" \
  -GmDir "$(w "$GM_DIR")" -Out "$(w "$WORK/ide-integrity.json")"
echo "== verificando assinaturas do runtime"
powershell -NoProfile -ExecutionPolicy Bypass -File "$(w "$HERE/tools/make-runtime-manifest.ps1")" \
  -RuntimeDir "$(w "$RT_DIR")" -Out "$(w "$WORK/gmide-runtime-integrity.json")"
echo "== verificando assinaturas do ProjectTool/Language Server"
powershell -NoProfile -ExecutionPolicy Bypass -File "$(w "$HERE/tools/make-tools-manifest.ps1")"   -GmDir "$(w "$GM_DIR")" -Out "$(w "$WORK/gmide-tools-integrity.json")"

# 2) Zip (nao ha zip/7z no Git Bash): System.IO.Compression via PowerShell.
cat > "$WORK/zip.ps1" <<'PS1'
param([string]$GmDir, [string]$RtDir, [string]$RtVer, [string]$PrefabsDir,
      [string]$IdeManifest, [string]$RtManifest, [string]$ToolsManifest, [string]$Out)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$sha = [System.Security.Cryptography.SHA256]::Create()
$files = [ordered]@{}
$total = 0L
$tmp = "$Out.tmp"
if (Test-Path $tmp) { Remove-Item $tmp -Force }
$fs = [System.IO.File]::Open($tmp, 'CreateNew')
$zip = New-Object System.IO.Compression.ZipArchive($fs, [System.IO.Compression.ZipArchiveMode]::Create)
function Add-One([string]$src, [string]$name) {
  $s = [System.IO.File]::OpenRead($src)
  try { $h = [BitConverter]::ToString($sha.ComputeHash($s)).Replace('-', '') } finally { $s.Dispose() }
  $e = $zip.CreateEntry($name, [System.IO.Compression.CompressionLevel]::Optimal)
  $e.LastWriteTime = (Get-Item -LiteralPath $src).LastWriteTime
  $in = [System.IO.File]::OpenRead($src); $o = $e.Open()
  try { $in.CopyTo($o) } finally { $o.Dispose(); $in.Dispose() }
  $len = (Get-Item -LiteralPath $src).Length
  $script:files[$name] = [ordered]@{ sha256 = $h; size = $len }
  $script:total += $len
}
function Add-Tree([string]$root, [string]$prefix, [string[]]$skip) {
  $root = (Resolve-Path -LiteralPath $root).Path.TrimEnd('\')
  $n = 0
  Get-ChildItem -LiteralPath $root -Recurse -File -Force | ForEach-Object {
    $rel = $_.FullName.Substring($root.Length + 1).Replace('\', '/')
    if ($skip -contains $rel) { return }
    Add-One $_.FullName "$prefix/$rel"
    $n++
    if ($n % 500 -eq 0) { Write-Host "  $prefix ... $n arquivos" }
  }
  Write-Host "  $prefix : $n arquivos"
}
Add-Tree $GmDir 'ide' @('ide-integrity.json', 'gmide-tools-integrity.json')
Add-One $IdeManifest 'ide/ide-integrity.json'
Add-One $ToolsManifest 'ide/gmide-tools-integrity.json'
Add-Tree $RtDir "runtime/$RtVer" @('gmide-runtime-integrity.json', '.gmide-complete')
Add-One $RtManifest "runtime/$RtVer/gmide-runtime-integrity.json"
if ($PrefabsDir) { Add-Tree $PrefabsDir 'runtime/Prefabs' @() }
$ideVer = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $GmDir 'IDE.dll')).FileVersion
$pkg = [ordered]@{
  format         = 1
  created        = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
  ideVersion     = $ideVer
  runtimeVersion = $RtVer.Substring('runtime-'.Length)
  runtimeFolder  = $RtVer
  fileCount      = $files.Count
  totalBytes     = $total
  files          = $files
}
$json = $pkg | ConvertTo-Json -Depth 4 -Compress
$e = $zip.CreateEntry('package.json')
$w = New-Object System.IO.StreamWriter($e.Open(), (New-Object System.Text.UTF8Encoding($false)))
$w.Write($json); $w.Dispose()
$zip.Dispose(); $fs.Dispose()
Move-Item -Force $tmp $Out
Write-Host "IDE $ideVer, $($files.Count) arquivos, $([math]::Round($total / 1MB)) MB descompactados"
PS1

PS=powershell
command -v pwsh >/dev/null 2>&1 && PS=pwsh
echo "== gerando $DIST/gmide-package.zip (alguns minutos)"
"$PS" -NoProfile -ExecutionPolicy Bypass -File "$(w "$WORK/zip.ps1")" \
  -GmDir "$(w "$GM_DIR")" -RtDir "$(w "$RT_DIR")" -RtVer "$RT_VER" \
  -PrefabsDir "${PREFABS_DIR:+$(w "$PREFABS_DIR")}" \
  -IdeManifest "$(w "$WORK/ide-integrity.json")" -RtManifest "$(w "$WORK/gmide-runtime-integrity.json")" \
  -ToolsManifest "$(w "$WORK/gmide-tools-integrity.json")" \
  -Out "$(w "$DIST/gmide-package.zip")"

if [ -f "$APK" ]; then
  cp "$APK" "$DIST/$PKG-Signed.apk"
else
  echo "Pacote base pronto. Agora compile o host em Release para embuti-lo no APK."
fi

cat > "$DIST/LEIA-ME.txt" <<'TXT'
GM IDE Android - APK completo
================================================

1. Baixe ou copie somente o APK completo para o celular.
   A IDE e o runtime ja vem dentro dele; nao precisa baixar um ZIP separado.

2. No celular, toque no APK para instalar (permita "instalar apps
   desconhecidos" para o app de arquivos, se o Android pedir).

3. Abra o app "GM IDE Android". Na primeira vez:
     - aguarde a instalacao automatica da IDE/runtime (1 a 3 minutos;
       o progresso aparece na tela). Nao precisa de internet;
     - reserve cerca de 1 GB livre alem do APK instalado;
     - conceda acesso aos arquivos quando for abrir ou salvar projetos.

Para atualizar a IDE: instale por cima com a mesma assinatura e preserve os dados.
Nao desinstale nem limpe os dados para uma atualizacao normal.
TXT

ls -la "$DIST"
