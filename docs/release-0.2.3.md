# GM IDE Android 0.2.3 — Windows em um único EXE

**Exportar → Windows → Gerar EXE** agora salva um único executável em Downloads.
Compartilhe somente esse arquivo. O player, loader e arquivos do jogo ficam
embutidos; a extração para o cache do Windows é automática, sem instalador ou
permissão de administrador. Nome, versão, ícone, dimensões e identificador dos
saves continuam configuráveis. Saídas Windows anteriores em ZIP passam a sugerir
a extensão `.exe`; reexporte para obter o novo formato.

O player continua baseado em WebView2 e exige Microsoft Edge WebView2 Runtime
no Windows x64. Windows VM/YYC nativo permanece como expansão futura.

O leitor verifica SHA-256 do pacote e dos arquivos extraídos. Extração usa
staging, publicação atômica e exclusão mútua entre cópias concorrentes. Arquivos
alterados ou ausentes no cache são reconstruídos. Os saves ficam separados,
vinculados ao identificador do jogo. Caches de versões anteriores permanecem
em `%LOCALAPPDATA%\GmGames\Packages` e podem ser removidos com o jogo fechado.
Assinatura Authenticode dos EXEs exportados ainda não é suportada.

## Instalação

Instale `gmide-android-0.2.3.apk` por cima da IDE existente. Não desinstale nem
limpe os dados. Pacote `io.gmide.android`, versão `0.2.3`, versionCode `5`, com
o mesmo certificado da versão anterior. Android ARM64 8.0+; prévia local 11+.

## Validação

- Build Release: 0 erros, 10 avisos; versão e assinatura conferidas.
- Atualização instalada no POCO F7 sem apagar dados. Tower Defense Template
  gerou `Tower_Defense_Template-windows.exe` em **20,4 s**, com **37.821.407 bytes**.
- Somente esse EXE foi copiado para uma pasta vazia no Windows. O player
  iniciou o runtime e renderizou o menu do jogo; captura conferida. A prévia
  Android também abriu. A execução foi verificada num Windows com WebView2
  instalado; não foi repetida uma partida completa nesta versão.
- Teste integrado do escritor C# com o leitor C++ real aprovou extração de assets
  de jogo, arquivos vazios e Unicode, reuso concorrente do cache, recuperação de
  corrupção do mesmo tamanho, rejeição de pacote adulterado e caminho `..`, e
  preservação do EXE anterior quando a nova exportação falha.
- APK Android, exportação Web, F5/F6/PiP, ícone personalizado e instalação limpa
  não foram revalidados nesta correção. A telemetria opcional foi preservada.

SHA-256 do APK:
`6006273a8abc525692658fe7ec9021b2e5daa613bfe534222ef489040005e815`.

SHA-256 do EXE de teste gerado no aparelho:
`d309c75251e766680004542a84eec1643ef4455ea61d3064a4620c95e650df54`.

Detalhes de formato, cache e limites: [Exportação Web e Windows](web-windows-export.md).
