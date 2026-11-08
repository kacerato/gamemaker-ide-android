param([string]$Cxx = 'g++.exe')
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$version = '1.0.3650.58'
$sdk = Join-Path $root 'sdk/package'
if (!(Test-Path (Join-Path $sdk 'build/native/include/WebView2.h'))) {
    New-Item -ItemType Directory -Path (Join-Path $root 'sdk') -Force | Out-Null
    $archive = Join-Path $root 'sdk/webview2.zip'
    Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/$version/microsoft.web.webview2.$version.nupkg" -OutFile $archive
    Expand-Archive -LiteralPath $archive -DestinationPath $sdk -Force
}
$out = Join-Path $root 'out'
New-Item -ItemType Directory -Path $out -Force | Out-Null
& $Cxx (Join-Path $root 'player.cpp') '-I' (Join-Path $sdk 'build/native/include') '-std=c++17' '-O2' '-s' '-municode' '-mwindows' '-static' '-static-libgcc' '-static-libstdc++' '-lole32' '-lshell32' '-luuid' '-lgdi32' '-o' (Join-Path $out 'GmGame.exe')
if ($LASTEXITCODE -ne 0) { throw 'Falha ao compilar o player Windows.' }
Copy-Item -LiteralPath (Join-Path $sdk 'build/native/x64/WebView2Loader.dll') -Destination $out
Copy-Item -LiteralPath (Join-Path $sdk 'LICENSE.txt') -Destination (Join-Path $out 'WebView2-LICENSE.txt')
Get-FileHash (Join-Path $out 'GmGame.exe'), (Join-Path $out 'WebView2Loader.dll') | Format-Table -AutoSize
