# GM IDE Android 0.2.1 — APK completo

**Baixe somente `gmide-android-0.2.1-release-completo.apk` (497,8 MB).**
A IDE e o runtime já vêm dentro do APK. Não precisa baixar, copiar ou escolher
`gmide-package.zip` separadamente.

Na primeira abertura, a instalação do conteúdo acontece automaticamente,
com avisos de progresso, sem internet. Reserve cerca de 1 GB livre além do APK
instalado para os arquivos extraídos. O pacote é lido diretamente de dentro do
APK, sem criar outra cópia do ZIP no cache e sem carregá-lo inteiro na memória.

Para atualizar, instale por cima. O pacote `io.gmide.android` e o certificado
anterior foram preservados; a IDE já instalada não é reextraída. Não desinstale
nem limpe os dados em uma atualização normal.

## Verificação desta versão

- Build **Release**, versão `0.2.1`, versionCode `3`, sem `debuggable`.
- Compilação final: **0 erros e 7 avisos preexistentes**.
- Assinatura APK v2/v3 verificada e igual à do release anterior.
- Asset embutido conferido como ZIP_STORED e idêntico ao pacote base.
- **17 verificações direcionadas aprovadas**: extração real de 5.405 arquivos do
  APK final, tamanho e SHA-256 de todos os destinos, preservação do runtime na
  segunda passagem, rejeição de corrupção/travessia e limites/ownership do stream.

SHA-256 do APK:
`57a45b77add7ac00f904193f9f6530884827f28bd681712c446c42bf93243afd`.

Nenhum aparelho estava conectado por ADB. A instalação e a primeira abertura
deste APK completo ainda precisam de aceite físico; a extração foi validada no
computador usando o mesmo extrator e o intervalo real do asset no APK final.

Permanecem os limites anteriores: ARM64, Android VM/APK; preview local exige
Android 11+. YYC e AAB não são suportados por este fluxo.
