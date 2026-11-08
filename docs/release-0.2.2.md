# GM IDE Android 0.2.2

Esta versão reúne exportação Web e Windows feita no Android, ajustes de retorno
do jogo e diagnóstico opcional de uso e falhas. O APK inclui a IDE e o runtime.

## Exportar

Abra o projeto e use **Exportar** na barra flutuante. Escolha Android, Web ou
Windows e configure nome, versão e ícone. Web gera um ZIP para hospedagem;
Windows gera um ZIP com executável x64 e os arquivos do jogo. Extraia o ZIP
inteiro no Windows. O player exige Microsoft Edge WebView2 Runtime.

O pacote web usa WebAssembly. O menu externo **Resume** do GX e a barra de
rolagem do console foram removidos; o jogo pausa ao minimizar e retoma ao voltar.
Os menus implementados pelo próprio projeto continuam sob seu controle.

Windows VM/YYC nativo fica para uma etapa posterior. Extensões nativas precisam
de implementação compatível com web. Esta versão não gera instalador EXE único,
AAB ou YYC.

## Diagnóstico opcional

O primeiro acesso apresenta a escolha de envio. A preferência pode ser alterada
em **Dados de uso e falhas** no menu da barra flutuante. O envio usa um
identificador aleatório; não envia projetos nem arquivos GML. Trechos de erro
podem conter nomes de objetos ou scripts. Desativar interrompe novos envios e
remove os dados locais da fila. Detalhes em [Telemetria](telemetria.md).

## Instalação

Instale `gmide-android-0.2.2.apk` por cima da versão existente, sem desinstalar ou
limpar dados. Pacote `io.gmide.android`, versão `0.2.2`, versionCode `4` e o mesmo
certificado da distribuição anterior. Android ARM64 8.0+; prévia local 11+.

## Validação e limites

- Build Release: 0 erros, 7 avisos; assinatura e versão verificadas.
- APK final atualizado no POCO F7 sem apagar dados. Tower Defense Template gerou
  Windows ZIP no próprio Android em 20,0 s (36.706.553 bytes). CRC de todos os
  arquivos, PE x64, opções e assets conferidos. A prévia abriu o jogo e Voltar
  retornou à IDE; as opções Windows foram persistidas no projeto.
- Exportação Web no aparelho: 21,1 s, jogo interativo; minimizar e retornar
  preservou a fase sem menu Resume do GX e sem barra de rolagem.
- Player Windows executado no PC com um pacote de teste; carregou o jogo.
  O ZIP específico gerado no POCO foi inspecionado, mas não teve sua execução
  repetida no Windows nesta validação final.
- Testes direcionados de empacotamento, recursos PE/ICO, persistência e lifecycle
  web aprovados. Backend: quatro testes e ingestão de evento sintético com
  repetição deduplicada. Não foi provocado um crash real no aparelho.
- F6/PiP e primeira instalação limpa não foram repetidos nesta publicação.

SHA-256 do APK:
`e9ab4d737e02be74b36acf088d4b909302bef55346be417eb61e9966a4f521ad`.

Arquitetura, requisitos e persistência: [Web e Windows](web-windows-export.md).
