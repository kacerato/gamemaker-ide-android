# Gerar APK do jogo no aparelho

Para executar sem instalar um APK, use o [preview Android local (F5)](run-preview.md).

Ao selecionar Android ou usar **Gerar APK do jogo**, a IDE salva o projeto aberto e captura a configuracao Android VM. A tela permite escolher nome, pacote, versao, icone e arquivo de saida.

VM e Default, quando Android esta selecionado no popup de alvos, usam a mesma entrada local. A interceptacao considera a selecao completa, evitando que esses cliques reapliquem Android pelo loader original de licencas. Chamadas duplicadas do seletor abrem somente uma sessao. O mapa dos outros pontos de login e suas continuacoes esta em [login-entrypoints.md](login-entrypoints.md).

Ao tocar em **Gerar APK**, a sessao executa o Igor/GMAssetCompiler embutido no processo separado `:build`, prepara o modulo Android e monta/assina o APK personalizado em `Download/<nome>.apk`. O runner Java/nativo vem do modelo precompilado; o codigo e os assets do projeto sao compilados no celular. Nao e exigido cache anterior. A tela recebe progresso e erros do compilador.

`AndroidGameCompile.cs` captura macros, preferencias e projeto salvo antes que a activity pause o SDL. O stub `libgmexec.so` transporta o pedido para o servico existente. O perfil explicito `GMIDE_LOCAL_ANDROID_EXPORT=1` habilita a execucao local do compilador apenas para Android VM; os demais alvos seguem o caminho original. O perfil e atualizado a cada pedido para nao permanecer ativo no proximo build.

O Igor expande o modulo do runner. No perfil local, a verificacao de caminhos SDK/JDK/NDK nao e necessaria: Gradle e substituido por `ApkBridge.RunGradle`, que usa o modelo embutido e `ApkBuilder`. Nao existem SDK/JDK/NDK ficticios. O empacotador aplica nome/pacote/versao/icone, alinha bibliotecas nativas e assina em APK v2.

As escolhas do popup sao passadas ao processo de build em um arquivo por exportacao. `RunGradle` monta e assina uma unica vez; o Igor termina suas copias e hooks normais. A sessao apenas publica esse APK em Downloads, usando arquivo temporario na pasta de destino antes de substituir uma versao anterior. Falhas preservam o APK anterior.

A janela reserva o estado `PrePackage` da IDE ate fechar (ou ate terminar uma geracao que estava em curso ao fechar), impedindo Run/exportacoes simultaneas. O servico tambem serializa a preparacao e execucao do Igor. O compilador tem limite total de 15 minutos; ao exceder, o stub e encerrado, e a ponte encerra o processo de build desconectado. Fechar a janela durante o build continua a geracao em segundo plano ate terminar ou atingir esse limite. O log completo e preservado no logcat, mas o popup recebe apenas etapas/erros, limitado a quatro atualizacoes por segundo.

Nome, pacote e versao sao lidos de `GMProject.Options` e gravados nas Game Options Android. Se esse recurso estiver ausente, ele e criado e registrado pela API real do projeto antes do primeiro salvamento. O arquivo de saida fica em `files/apk-export/last.json`. O popup mostra o icone atual do projeto; escolher outra imagem cria uma copia imutavel para o job e inclui sua importacao no registro pendente.

Como o SDL fica pausado com o popup aberto, as alteracoes das Game Options aguardam o proximo evento da IDE. Um registro pendente e gravado atomicamente em `last.json` antes do build, associado ao caminho do projeto. Ele so e removido apos um salvamento confirmado. Se o app encerrar antes desse evento, a proxima abertura da exportacao recupera e salva as escolhas no projeto. As opcoes do APK continuam sendo aplicadas no empacotamento atual.

## Descoberta de tipos no compilador

`GMAssetCompiler.dll` e `CoreResources.dll` usam o mesmo patch de isolamento de varreduras que o ProjectTool (`AssemblyScanPatches.cs`). As chamadas a `AppDomain.GetAssemblies()` passam a enumerar apenas o `AssemblyLoadContext` da ferramenta. Isso impede que a descoberta de opcoes tente resolver dependencias do host Android, como OSCore, durante a leitura do projeto.

O espelho `ac-<hash>-v3` contem copias gravaveis dessas duas DLLs. As demais dependencias continuam vinculadas ao runtime original. O marcador novo impede a reutilizacao do espelho antigo sem essa correcao. Uma falha na preparacao e reportada antes de executar o compilador, sem retornar silenciosamente ao original.

A regressao focada `tools/build-test/assembly-scan-test` reproduziu a falha OSCore com um assembly do host contendo dependencia ausente. Com as DLLs corrigidas do runtime Windows 2026.0.0.23, a descoberta concluiu com 17 plataformas, incluindo Android; os originais permaneceram intactos e a reaplicacao nao encontrou chamadas restantes. Isso valida a descoberta de tipos no host, sem comprovar a exportacao completa no aparelho.

## Assinatura

O popup le `machine.Platform Settings.Android.Keystore.filename` em `<userDir>/local_settings.json`, com prioridade sobre os defaults do runtime, seguindo a precedencia do Igor. O caminho mostrado e comparado com o realmente usado no empacotamento. Caminho configurado ausente, alias invalido ou senha incorreta causam erro; a chave nao e trocada silenciosamente.

Somente sem keystore configurado e criado `Documents/GameMakerIDE/gmide-release.p12` (RSA 2048, validade de 30 anos). A senha fica em `gmide-release.p12.json` ao lado. Guarde os dois arquivos para atualizar jogos com a mesma assinatura. A instalacao do APK gerado e opcional e passa pela confirmacao do Android.

No perfil local, os dois getters do Igor (`GetKeystorePassword` e `GetKeystoreAliasPassword`) usam `ApkBridge.ReadLocalKeystorePassword`, antes de acessar a licenca. Para a chave padrao, os getters devolvem vazio: `AndroidSigning.Load` le a senha propria do arquivo `.p12.json`. Para uma chave personalizada, usam o snapshot tipado produzido por `WriteAndroidPreferences`, onde a camada de Preferencias ja decifrou as senhas. A senha de alias vazia reutiliza a senha do keystore. O caminho do snapshot e transmitido pelo ambiente privado do job, sem copiar senhas para argumentos ou mensagens de progresso. Credenciais de outra chave sao recusadas.

O marcador `.gmide-igor-patch-7` invalida o cache antigo e inclui os ajustes do preview Android local. A regressao `keystore-password-test` carrega o Igor real: o original falha com licenca nula e os dois getters corrigidos funcionam sem ela. Isso nao recupera uma senha antiga cujo contexto de decifragem ja nao esteja disponivel na IDE; nesse caso e preciso configurar as credenciais corretas da mesma chave nas Preferencias.

`AndroidSigning.Load` converte o retorno `IKey` de `KeyStore.GetKey` com `JavaCast<IPrivateKey>()`. O cast C# `as IPrivateKey` nao reconhece necessariamente a interface Java implementada pelo objeto e produzia um falso erro de chave privada ausente. Se o formato do keystore ja foi carregado, erros de alias/senha/chave sao reportados diretamente, sem serem mascarados por uma tentativa posterior de BKS.

Para atualizar a IDE ja instalada no aparelho de desenvolvimento, o APK de `dist` foi assinado com o certificado existente (SHA-256 `d7a6e1857b57be8cffe72569e22077b561debab4d60b80c006655321d16e5086`). O keystore padrao do SDK Windows deste ambiente tem outro certificado; um novo build precisa da assinatura correspondente para atualizar esse aparelho sem desinstalar. Essa assinatura da IDE e independente da chave dos jogos em Documents/GameMakerIDE.

## Configuracoes do jogo e auditoria no aparelho

A engrenagem da barra superior abre Game Options. O mesmo destino aparece em Assets > Quick Access > Game Options. No editor Android, a orientacao pertence a Android > General > Build Settings > Orientation: Portrait, Portrait-flipped, Landscape e Landscape-flipped. Para jogo somente horizontal, desative as duas opcoes Portrait e mantenha Landscape (Landscape-flipped e opcional). Salve o projeto e gere outro APK; o APK instalado anteriormente nao muda.

O Igor escreve essas quatro escolhas em metadados OrientPortrait/OrientLandscape/OrientPortraitFlipped/OrientLandscapeFlipped e em options.ini. O empacotador preserva o manifesto expandido pelo Igor. No Idle Game Template exportado em 07/11/2026 as quatro opcoes estavam True no log e -1 no manifesto final: a rotacao nos dois eixos corresponde a essa configuracao.

Icons e Adaptive Icons controlam os icones do projeto; Images controla splash screens; Permissions controla as permissoes do manifesto. A assinatura e global em Preferences > Platform Settings > Android > Keystore. Nome/pacote/versao e icone do popup retornam ao projeto pelo registro pendente descrito acima. `AndroidProjectIcons.Import` grava seis densidades regulares e seis foregrounds adaptativos em `options/android/<config opcional>/gmide-icons/v1-<hash>/` e atualiza as propriedades reais das Game Options. Os backgrounds adaptativos existentes sao preservados. Os arquivos pertencem ao projeto, sobrevivem a copia/reabertura e usam nomes por conteudo para preservar undo e repeticao do registro pendente. Um icone local do popup antigo e migrado antes de abrir a nova exportacao, e o arquivo antigo e renomeado somente depois de salvar com sucesso. A acao "Usar o do jogo" descarta somente a nova escolha no popup; a exportacao seguinte respeita as alteracoes feitas nas Game Options.

`AndroidOptionsPatches` acrescenta Android tanto a `GMProjectAdapter.VerifyLicensedOptions` (criacao do recurso quando o projeto carrega) quanto a `GetLicensedOptions` (lista do editor). As demais plataformas continuam seguindo seus criterios existentes. O marcador composto `processhelper-sh-v1+unsafe-location-v1+local-android-options-v1` aplica os tres patches de IDE.dll a partir do mesmo original. A regressao `apk-options-test` verifica o patch no IDE.dll real e o round-trip de icone, orientacao e permissao no contrato de dados de GMAndroidOptions.

O carregamento tambem precisa de `CoreResources.dll`: `GMProject.GetOptionTypeNamesList` inclui `GMAndroidOptions` uma unica vez antes de aplicar a compatibilidade de versao do ProjectTool. Sem esse terceiro ponto, reabrir um projeto sem conta ignorava o arquivo Android existente e `VerifyLicensedOptions` podia recriar seus valores padrao. O marcador `local-android-options-load-v1` corrige somente a copia usada pela IDE; os binarios originais sao preservados. A regressao executa esse metodo no CoreResources real com uma fonte de modulos vazia.

Validacao no POCO em 07/11/2026: Android apareceu no editor e no Asset Browser. Uma copia independente do template (`Documents/GMIDE_Export_Validation`, pacote `com.gmide.validation`) foi configurada com Portrait/Portrait-flipped desativados. Pelo picker do popup foi escolhido um PNG do template; a primeira geracao terminou em 13,6 s. Ao fechar o popup e retomar a IDE, as 12 imagens foram gravadas no projeto e suas referencias apareceram em options_android.yy. Depois de encerrar e reabrir a IDE, o popup mostrou o PNG do projeto e uma segunda geracao, sem icon_path no pedido, terminou em 6,7 s. Os 13 recursos de icone do APK final coincidiram com os PNGs do projeto (drawable/icon.png usa xhdpi, conforme o Igor). A assinatura v2 foi verificada com apksigner; o APK foi instalado e abriu a tela inicial do jogo. O runner registrou RestrictOrientation com apenas as orientacoes horizontais permitidas. Evidencias: logs/android-options-device.txt, logs/android-options-regression/ e logs/gmide-validation-game.png. O projeto original foi reaberto ao final.

## Limites

- Android VM e APK. YYC e AAB nao sao suportados por este fluxo.
- O modelo corresponde ao runtime `runtime-2026.0.0.23`.
- Extensoes com codigo Java/Kotlin proprio exigem um modelo que inclua seu dex. Bibliotecas `.so` do modulo entram no APK.
- Validacao atual: compilacao do host, regressao focada de isolamento da descoberta de tipos, `apk-options-test` e `keystore-password-test`. No POCO 25053PC47G, Idle Game Template gerou Downloads/Idle_Game_Template.apk em 6,8 s (36.113.091 bytes); apksigner verificou a assinatura v2 e o Android aceitou a instalacao. Evidencias em logs/gmide-adb-after-keystore-fix.txt e logs/keystore-password-regression/evidence.json. Isso nao comprova todas as configuracoes, a recuperacao do registro pendente ou compatibilidade com outros aparelhos.
