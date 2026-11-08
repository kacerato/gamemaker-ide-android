# Instalação a partir do APK completo

Desde a versão 0.2.1, a IDE e o runtime vêm em
`assets/bootstrap/gmide-package.zip` no próprio APK. O usuário instala um arquivo
e abre o app. A primeira abertura extrai e verifica os arquivos automaticamente.
Não há download adicional, token GitHub ou seleção de ZIP nesse fluxo.

## Cadeia de instalação

`GmIde.Android.csproj` exige o pacote base e o inclui como `AndroidAsset`.
`AndroidStoreUncompressedFileExtensions` mantém `.zip` sem compressão externa.
O build não produz silenciosamente uma versão incompleta se o pacote faltar.

`Launcher.Run` chama `PackageImport.TryInstall` antes de carregar a IDE.
O importador prefere o pacote embutido. `AssetManager.OpenFd` fornece descritor,
offset e comprimento; um descritor duplicado pertence ao `FileStream` e
`PackageAssetStream` limita leitura e seek ao intervalo do ZIP dentro do APK.
Assim não é necessário copiar o ZIP para cache nem carregá-lo inteiro na memória.

O desenho segue a API de [assets sem compressão do Android](https://developer.android.com/reference/android/content/res/AssetManager#openFd(java.lang.String))
e o uso de descritor/offset/comprimento observado no
[carregamento de assets do Chromium](https://chromium.googlesource.com/chromium/src/+/refs/heads/main/base/android/java/src/org/chromium/base/ApkAssets.java).

`PackageExtractor` mantém o fluxo existente: confere espaço disponível, verifica
SHA-256 dos arquivos, extrai para diretórios `.importando` e publica a IDE por
último. Uma interrupção antes da conclusão permite repetir a extração ao reabrir.
O progresso usa os avisos existentes; a extração roda fora da thread de UI.
Arquivos de projetos não são destinos do extrator.

O destino compartilhado do bootstrap e da IDE no Mono/Android e
`<externalFiles>/home/GameMakerStudio2-LTS2026`. `XDG_DATA_HOME` nao muda o
CommonApplicationData usado pelas macros da IDE neste host. A instalacao 0.2.1
usava `home/.local/share/...` e podia deixar a IDE baixar outro runtime em
`home/...`, sem as ferramentas gerenciadas e sem o manifesto de integridade.
Nesse estado, F5 falhava com "Corrupt or damaged GameMaker build tools" antes
de iniciar o compilador.

O bootstrap agora usa `PackageExtractor.SystemDirectoryFor`. No boot,
`RuntimeIntegrity.RestoreFromLegacy` confere as tres ferramentas do runtime
anterior e recupera os arquivos ausentes no destino efetivo, mesmo se ja existir
um receipt.json de download Linux. O manifesto e publicado por ultimo. A origem
e os arquivos locais existentes sao preservados; ferramentas principais que
divergirem dos hashes verificados sao restauradas. A recuperacao e idempotente
e uma origem invalida e recusada antes da copia. RuntimeImport tambem sincroniza
os Prefabs anteriores e instala o modulo Android embutido no destino correto.

Instalações que já têm a IDE não são reextraídas durante uma atualização normal.
A ausência de permissão de acesso amplo não impede ler o pacote embutido: os
destinos pertencem ao aplicativo. O acesso a projetos segue os seletores existentes.
O importador legado de ZIP/ADB continua no código para versões de desenvolvimento;
uma falha no asset presente é exibida como falha de instalação, sem abrir o picker
ou pedir uma pasta ADB como se o pacote não existisse.

## Custos e validação

O APK completo tem aproximadamente 500 MB. O conteúdo extraído ocupa cerca de
800 MB; reserve cerca de 1 GB livre além do APK instalado. A cópia do APK baixado
também pode continuar na pasta Downloads até o usuário apagá-la.

A regressão `tools/pkg-test` aceita `<apk> <saida> <offset> <length>` e executa o
extrator real sobre o intervalo do pacote no APK final. Ela cobre extração,
integridade, preservação do runtime existente, rejeição de corrupção/travessia e
limites/ownership do stream. A presença e o método ZIP_STORED do asset também
são conferidos no APK produzido. Isso não substitui validar o descritor Android,
a instalação e a primeira abertura em aparelho físico.

Para verificar o APK completo sem calcular os offsets manualmente:

```sh
python tools/pkg-test/verify-bundled-apk.py dist/gmide-android-0.2.1-release-completo.apk
```

O verificador compara o pacote embutido com a entrada do build e usa uma pasta de
saída nova para o teste. Os projetos e os dados existentes permanecem intactos.

## Correcao do caminho do runtime — 08/11/2026

No POCO 25053PC47G, a instalacao 0.2.1 extraiu corretamente o pacote em
`home/.local/share/...`, mas a IDE usou `home/...`. Depois do download de um
runtime Linux nessa segunda pasta, o F5 do Tower Defense Template falhou em
14:30:52 com manifesto ausente e "Corrupt or damaged GameMaker build tools";
o compilador nao chegou a iniciar.

O APK `gmide-android-0.2.1-runtimefix.apk` foi instalado por cima, com a mesma
assinatura. No boot, a recuperacao automatica trouxe 1.082 arquivos e o modulo
Android embutido instalou 77 arquivos na pasta correta. O primeiro F5 do mesmo
projeto iniciou em 14:48:23.988 e o runner confirmou Native preview ready em
14:48:49.028 (25 segundos). O cenario foi renderizado e a partida avancou ate a
segunda onda. A IDE permaneceu no PID 26710 e o runner no PID 11004; o preview
foi deixado aberto. Stop e um segundo F5 nao foram repetidos nessa partida.

Validacao no PC: 21 checks direcionados aprovados, incluindo extracao e hashes
dos 5.405 arquivos, caminho canonico, recuperacao apesar de receipt Linux,
preservacao de arquivos locais, idempotencia e rejeicao de origem invalida.
Build Release: 0 erros e 10 avisos. A primeira tentativa de extracao no Windows
encontrou bloqueio transitorio ao renomear Prefabs; a repeticao em pasta nova
concluiu os checks. A assinatura do APK corrigido foi verificada com apksigner.

SHA-256: `E21E987EAAD2508D7A5B23B8B58D996A2E664233F3AC00FD3F105B3D04673485`.
Evidencias: `logs/gmide-template-f5-latest.txt`,
`logs/runtime-path-device-validation.txt`, `logs/runtime-path-regression.txt`,
`logs/runtime-path-build.txt`, `logs/tower-defense-f5-runtimefix.png` e
`logs/tower-defense-runtimefix-current.png`.
