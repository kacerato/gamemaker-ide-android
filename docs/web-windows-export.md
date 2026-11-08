# Exportacao Web e Windows pelo Android

O botao **Exportar** abre o exportador com tres destinos: Android APK, Web ZIP e
Windows EXE. Toda geracao usa o compilador do aparelho. O PC nao participa da
exportacao; ele so e usado ao desenvolver o modelo precompilado do player.

## Destinos e dependencias

- Android: preserva o runner VM e a assinatura do exportador APK.
- Web: usa o modulo `operagx` VM do runtime 2026.0.0.23, com runner WebAssembly
  precompilado. O ZIP inclui `index.html`, scripts, WASM, assets e `game.unx`.
  Hospede o conteudo extraido em um servidor web; abrir `index.html` por arquivo
  nao e o fluxo suportado. O botao **Previa web** abre os arquivos exportados na
  IDE, em uma origem HTTPS local isolada por identificador de armazenamento.
- Windows: usa os mesmos arquivos web dentro de um player Win32 x64 com WebView2.
  Extraia o ZIP inteiro e abra o EXE. Precisa do Microsoft Edge WebView2 Runtime;
  se estiver ausente, o player oferece abrir o site oficial. Nao exige .NET,
  GameMaker, SDK nem servidor no computador de quem joga.

Windows VM/YYC nativo, instalador EXE unico e HTML5 JavaScript tradicional nao
estao implementados. Windows VM fica como expansao futura, incluindo a cadeia
de compilacao de shaders Direct3D no Android. Extensoes nativas Windows/Android
nao se tornam extensoes WebAssembly automaticamente; o projeto precisa oferecer
uma implementacao para web. Integracoes exclusivas do portal GX nao sao
fornecidas pelo player local.

## Configuracao e persistencia

Nome e versao partem das Game Options de cada destino. Alteracoes sao aplicadas
e salvas quando a thread da IDE retoma. A configuracao Windows corresponde ao
player web, nao a todos os controles disponiveis para o Windows VM nativo.
Identificador dos saves, dimensoes da janela e icones sao salvos em
`options/gmide-export/settings.json` e arquivos adjacentes, dentro do projeto.
O icone Windows tambem atualiza `option_windows_icon`. Caminhos de saida ficam
nas preferencias privadas da IDE. Preserve o identificador nas atualizacoes
para manter os saves. O player Windows guarda os dados em
`%LOCALAPPDATA%\GmGames\<identificador>`.

O EXE recebe icone e informacoes de versao por escrita gerenciada de recursos PE,
sem chamadas a ferramentas Windows durante a exportacao. O modelo e x64; ele
nao deve ser assinado antes dessa personalizacao. Assinatura Authenticode dos
jogos nao foi adicionada.

## Lifecycle e interface do jogo

O pacote distribuido remove o console desktop do runner GX e a rolagem que ele
provocava. O canvas usa o ajuste de proporcao original do runner. Ao minimizar,
o jogo pausa; ao retornar, retoma sem o menu externo **Resume** do GX. Menus e
pausas implementados pelo proprio jogo permanecem sob controle do projeto.
No Windows, F11 alterna tela cheia; fechar a janela encerra o player. A acao de
sair da pagina do runner pede o fechamento da janela via mensagem local.

## Arquitetura e verificacao

`AndroidGameCompile.Prepare` captura o contexto do projeto na thread da IDE.
`WebExportPackage.Prepare` cria um job com macros, cache, temporarios e saida
exclusivos. `WebGameCompile` chama `OperaGX Package` no servico `:build`, aplica
as escolhas e publica um ZIP atomicamente. O timeout e 15 minutos; a reserva de
build e compartilhada com APK/F5 para impedir concorrencia no compilador.
Os originais do runtime permanecem intactos; os patches usam copias verificadas.

O teste `tools/build-test/web-export-test` compila um projeto real usando os
mesmos escritores de patch e empacotadores, num worker de PC que simula somente
o predicado Android da admissao ao compilador. Isso nao comprova execucao ARM64,
audio nativo ou permissoes do aparelho. O aceite fisico deve incluir gerar Web e
Windows desde um projeto sem cache no Android, abrir a previa, minimizar/voltar,
reabrir o projeto e executar o ZIP gerado no Windows.

O player se recompila com `tools/windows-player/build.ps1` (MinGW-w64 g++, SDK
WebView2 1.0.3650.58 da NuGet oficial). O SDK fica ignorado pelo Git; os tres
arquivos em `out` sao incluidos no APK, com a licenca da Microsoft.

Referencias:
- [Microsoft: conteudo local e origem HTTPS em WebView2](https://learn.microsoft.com/microsoft-edge/webview2/concepts/working-with-local-content)
- [Microsoft: distribuicao do WebView2 Runtime](https://learn.microsoft.com/microsoft-edge/webview2/concepts/distribution)
- [GameMaker: compilacao e exportacao local](https://manual.gamemaker.io/lts/en/Introduction/Compiling.htm)
