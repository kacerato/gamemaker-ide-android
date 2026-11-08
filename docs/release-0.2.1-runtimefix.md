# GM IDE Android 0.2.1 — correção do runtime e F5

**Baixe somente `gmide-android-0.2.1-runtimefix.apk` (aproximadamente 498 MB).**
Esta release inclui a IDE e o runtime no APK e corrige o erro
**“Corrupt or damaged GameMaker build tools”** ao executar um template com F5.

## Correção

O instalador extraía o runtime em `home/.local/share/...`, enquanto a IDE
procurava em `home/...`. Isso permitia um segundo download Linux, sem o manifesto
e as ferramentas necessárias ao preview local. A primeira instalação agora usa
a mesma pasta da IDE. Instalações anteriores recuperam automaticamente o runtime
completo já presente no aparelho, após conferir seus hashes.

## Instalar ou atualizar

Instale o APK por cima do aplicativo existente, com a mesma assinatura.
**Não desinstale nem limpe os dados.** Projetos e arquivos existentes são
preservados. Abra a IDE e aguarde a recuperação automática na primeira abertura.
Não é necessário baixar ou selecionar um ZIP separado.

O APK mantém versão Android `0.2.1`, versionCode `3` e pacote
`io.gmide.android`; esta é uma publicação de correção com a tag
`v2026.11.08.2`. O certificado é o mesmo da release anterior.

## Validação

- Build Release: **0 erros e 10 avisos**; assinatura APK verificada com apksigner.
- **21 verificações direcionadas aprovadas**, incluindo extração e hashes de
  5.405 arquivos, caminho correto, recuperação, preservação de arquivos,
  idempotência e rejeição de origem inválida.
- Atualização instalada no **POCO 25053PC47G** sem apagar os dados. A recuperação
  restaurou 1.082 arquivos; o módulo Android acrescentou 77 arquivos no destino.
- **Tower Defense Template:** F5 compilou e abriu o preview em aproximadamente
  **25 segundos**. O jogo respondeu ao toque e chegou à segunda onda.

O preview foi deixado aberto. Stop e um segundo F5 não foram repetidos nesta
partida. A primeira instalação limpa deste APK não foi repetida no aparelho;
o caminho de extração foi validado no computador e a recuperação foi validada
fisicamente. F6 e outros aparelhos não foram revalidados nesta correção.

SHA-256 do APK:
`e21e987eaad2508d7a5b23b8b58d996a2e664233f3ac00fd3f105b3d04673485`.

Limites mantidos: ARM64, Android VM/APK, preview local a partir do Android 11.
YYC e AAB não são suportados neste fluxo.

Detalhes técnicos e cenário de aceite em [Instalação embutida](embedded-installation.md).
