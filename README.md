# GM IDE Android

Port pessoal da IDE do GameMaker para Android ARM64, com integração de toque,
teclado, compilação local Android VM, preview e exportação de APK.

## Instalar ou atualizar

Baixe o APK Release em [Releases](https://github.com/kacerato/gamemaker-ide-android/releases/latest).
A versão 0.2.0 aparece no launcher como **GM IDE Android**.
Para atualizar uma instalação com a mesma assinatura, instale por cima e preserve
os dados. Não desinstale o aplicativo para uma atualização normal.

A primeira instalação também precisa do pacote de IDE/runtime
`gmide-package.zip`, preparado com uma instalação local do GameMaker LTS 2026.
Coloque-o em Downloads ou escolha-o quando o aplicativo solicitar. Acesso a todos
os arquivos e cerca de 1 GB livre são necessários para importar o conteúdo.
Esse port não substitui a licença e os termos do GameMaker.

## Conteúdo

- `host/`: host .NET Android, integração da IDE, Java/SDL e empacotador APK.
- `native/`: código nativo, fontes de terceiros e dependências ARM64 pré-compiladas.
- `tools/`: preparação de pacotes/modelos, ferramentas de análise e regressões focadas.
- `demo/`: código, scripts e vídeo do cenário Fazendinha.
- `docs/`: arquitetura, funcionamento, limitações e evidências registradas.

As fontes externas em `native/src/` são incluídas como arquivos normais, com suas
licenças originais. Chaves, credenciais, logs, caches e intermediários de build
ficam fora do Git. Os binários necessários ao host e o modelo de exportação são
incluídos; assemblies proprietários da IDE são fornecidos pela instalação local.

## Compilar

Ambiente atual: Windows, .NET SDK 8 com workload Android, Android SDK 34/35,
JDK 21, NDK 27.1 e GameMaker LTS 2026 instalado. Os caminhos configuráveis estão
em `host/GmIde.Android.csproj` e nos scripts nativos.

```sh
bash host/build-java.sh
dotnet build host/GmIde.Android.csproj -c Release -p:AndroidDebuggable=false
```

`Release` define a configuração de compilação; a chave de assinatura é uma
configuração independente. Para atualizar instalações anteriores, preserve o
mesmo certificado. Não adicione keystores ou suas senhas ao repositório.

Para recompilar dependências, consulte `native/build-native.sh`,
`native/extras/README.md`, `native/magick/`, `native/ffmpeg/`,
`tools/apk-template/make-template.py` e `tools/runner-host/make-runner.py`.
Os geradores de template/runner dependem do runtime GameMaker 2026.0.0.23.

## Funcionamento e limites

- [Exportação Android VM e assinatura](docs/apk-export.md).
- [Preview local e depuração](docs/run-preview.md).
- [Interação mobile e validações registradas](docs/mobile-ui.md).
- [Auditoria do port](docs/auditoria-port.md).
- [Cenário Fazendinha](docs/teste-fazendinha.md).

O fluxo de exportação suporta APK Android VM. YYC e AAB não são suportados.
Extensões Java/Kotlin precisam estar integradas ao modelo do runner.
As validações em aparelhos descritas nos documentos são históricas e não
equivalem à aceitação de cada novo APK; confira as notas do release.
