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
