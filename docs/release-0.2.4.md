# GM IDE Android 0.2.4 — Interface para celular e gestos

## Interface para celular (opcional)

Nova casca mobile-first, ligada no menu "⋯" do botão de atalhos ("Interface para celular").
A interface desktop continua sendo a padrão, e dá para voltar a ela pelo ⋮ da casca
("Interface clássica (desktop)"). A escolha é aplicada na hora, sem reiniciar.

- Barra superior com o projeto, Game Options, Exportar, Executar, Depurar e Parar; o ⋮ dá acesso a todos os menus.
- Trilho lateral com Inspetor, Recursos, Saída, Busca, Versões e Pausas, cada um abrindo como gaveta.
  Voltar fecha a gaveta; abrir um editor fecha Recursos.
- Janela flutuante em foco ganha alça de redimensionar e o botão "No workspace", que a leva para o workspace.

Detalhes: [Interface mobile](mobile-ui.md).

## Gestos

- Dois dedos arrastam a room, o sprite, o tileset e o workspace 1:1, inclusive para os lados.
- A pinça dá zoom contínuo no workspace, também com os dedos sobre janelas de editores. Abrir um
  editor não volta mais o zoom para 100%.
- O gesto nativo de dois dedos do SDL, que travava a rolagem, foi desligado.
- Mouse Bluetooth/USB: arrastar com o botão direito sobre canvas e workspace move a vista; direito
  segurado + roda dá zoom; clique direito sem arrastar continua abrindo o menu de contexto.

## Telemetria

Os lotes de telemetria opcional eram recusados pelo servidor (`400 {"error":"json"}`), porque o app
gravava o JSON com BOM UTF-8. O app agora grava sem BOM, e o servidor, já atualizado, ignora o BOM das
versões anteriores.

## Instalação

Instale `gmide-android-0.2.4.apk` por cima da IDE existente. Não desinstale nem limpe os dados.
Pacote `io.gmide.android`, versão `0.2.4`, versionCode `6`, com o mesmo certificado da versão anterior.
Android ARM64 8.0+; prévia local 11+.

## Validação

- Build Release limpo: 0 erros, 10 avisos. Versão e assinatura v3 conferidas.
  SHA-256 do certificado: `d7a6e1857b57be8cffe72569e22077b561debab4d60b80c006655321d16e5086`.
- SHA-256 do APK: `0f698a886ed76bd347c7e65ffd4db76b70757201b4e76dc4ce1fe7f67835550e`.
- Instalado por cima no POCO F7 (09/11/2026) sem apagar dados; a IDE abriu com a casca e o projeto.
- Gestos e mouse validados no aparelho durante o desenvolvimento (`docs/mobile-ui.md`).
  O zoom com direito + roda do mouse ainda não foi testado no aparelho.
