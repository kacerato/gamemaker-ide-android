# Interface mobile da IDE

Camada do port que adapta a IDE de desktop a uma tela de celular em paisagem, sem reescrever editores. Nasceu dos problemas do teste da fazendinha (`docs/teste-fazendinha.md`). Princípio: corrigir na causa (contrato errado, evento no quadro errado, padrão de desktop) e usar os mecanismos que a própria IDE já tem (preferências, zoom do workspace, `ClipScale`, `ScrollBar`), em vez de simular cliques.

## Peças

| Arquivo | Papel | Thread |
|---|---|---|
| `TouchInput.cs` | Gestos → eventos de mouse do SDL | UI do Java / filtro do SDL |
| `MobileScroll.cs` | Rolagem direta 1:1 com inércia | acumula na UI, aplica na IDE |
| `MobileFrame.cs` | Um ponto por volta do loop da IDE (`WindowManager.OnProcess`) | IDE |
| `MobileWindowPolicy.cs` | Editores em 100% com a vista seguindo o foco; diálogos na desktop e dentro da tela | IDE |
| `MobileWindows.cs` | Fachada para os ganchos de IL (sem tipos da IDE nas assinaturas) e modo Mover | IDE |
| `MobileUiPatches.cs` | Patches de IL do IDE.dll para a camada | boot |
| `KeyboardMode.cs` | Modo de edição de código com teclado | IDE |
| `IdeSetup.cs` | Foco → teclado; Enter fecha o teclado em campo de uma linha | IDE |
| `GmIdeActivity.java` | Superfície acima do teclado, barra de eco, barra de símbolos, vibração | UI |
| `SDLActivity.java` | Tipo de entrada do IME (sem sugestões; modo código) e `gmCommitText` | UI |
| `AndroidFileDialogs.cs` | Seletor de arquivos com o mesmo contrato do Linux | IDE |

## Toque

- **Toque rápido:** clique. O "solta" só entra na fila do SDL depois que a IDE roda 2 quadros com o botão pressionado (`TouchInput.OnIdeFrame`, chamado por `MobileFrame`). A IDE lê a fila inteira por quadro e só chama `OnMouseOver` com o estado do fim do quadro. A paleta de tiles e o lápis, a borracha e o auto-tile das camadas de tiles agem só ali (`RoomEditor_TileDisplay.TileDisplay_SelectTiles`). Um toque entregue como aperta+solta na mesma leitura nunca selecionava nada. Se a IDE não rodar quadros, um timer libera o "solta" em 250 ms.
- **Arrastar numa área rolável** (listas, árvores, Inspetor, Preferências, editor de código): rolagem direta. O conteúdo acompanha o dedo 1:1 e continua com inércia ao soltar.
  - O alvo é o `ScrollBar` do gadget sob o dedo, e a rolagem muda `Slider.SliderValue`, o mesmo caminho de arrastar o polegar da barra.
  - Um toque durante a inércia só a para, sem clicar.
  - Antes, o arraste virava "notches" de roda (24 dp cada), e cada notch andava `WheelIncrement` unidades, que varia por painel: no Inspetor, um gesto rolava poucos pixels.
- **Arrastar fora de área rolável** (canvas da sala, do sprite e do tileset; workspace): arrastar com o botão esquerdo.
- **Toque longo (500 ms):** vibra e "arma".
  - Soltar sem mover: clique direito (menu de contexto).
  - Mover depois de armar: arrastar com o esquerdo a partir do ponto do toque. Serve para arrastar e soltar recursos e para selecionar texto no editor de código, onde arrastar rola.
  - Antes, o clique direito saía aos 500 ms, e segurar antes de arrastar abria o menu de contexto.
- **Dois dedos:** rolagem e pinça, como antes.

## Janelas

1. **Editores no workspace em 100%.** O antigo ajuste afastava o zoom até a janela caber, o que deixava tudo minúsculo. Agora:
   - Ao abrir um editor (`AddChainRoot`), os painéis laterais expandidos recolhem, como gavetas. Tocar na aba do painel o abre de novo.
   - A vista vai até a janela em zoom 100%.
   - A cada troca da janela focada no workspace, se ela não estiver visível, a vista anda até ela (`MobileWindowPolicy.FollowFocus`).
2. **Código em aba cheia por padrão.** `MobileUiPatches.PatchMobileCodePrefs` muda só os *padrões* de `GeneralSettings`. Quem já salvou outro valor nas Preferências mantém a própria escolha.
   - Liga `cloud.Text Editors.OpenObjectEventsFullScreen`, `OpenScriptsFullScreen`, `OpenShadersFullScreen` e `FocusPan`.
   - Eventos e scripts abrem numa única aba de código, com uma sub-aba por evento. Nenhuma janela de código é encadeada para fora da tela.
3. **Diálogos na desktop.** Diálogos de editor (`Window.AddWindow` com tipo `*Dialog`, por exemplo o `StripImportDialog` de "Converter para quadros") sobem para a desktop (`WindowManager.AddWindow`) e fecham junto com o dono.
   - Eram centralizados no painel do editor, que é menor que eles.
   - O centro calculado (`GUILayout.CalculatePosition`) ficava negativo e o diálogo aparecia cortado, com o zoom do workspace.
4. **Tudo dentro da tela.** Toda janela flutuante da desktop é registrada (`WindowManager.AddWindow` → `MobileWindowPolicy.OnWindowAdded`) e mantida dentro dela.
   - Janelas redimensionáveis encolhem e o conteúdo reflui.
   - Diálogos de tamanho fixo têm o conteúdo reduzido só o necessário, por `ClientPanel.Layout.ClipScale` (o mesmo mecanismo do zoom do workspace, então clique e layout continuam coerentes).
   - A janela pode ser arrastada; se sair da tela, volta.

## Teclado

- **Tipo de entrada:** todo campo da IDE abre o teclado com `TYPE_TEXT_FLAG_NO_SUGGESTIONS`.
  - Com o editor de código focado, também `TYPE_TEXT_VARIATION_VISIBLE_PASSWORD`, respeitada inclusive por teclados que ignoram a flag. O resultado é sem autocorreção e sem maiúscula automática.
  - O Gboard trocava "else" por "eles", "floor" por "flor" e "dir" por "Dir", porque o `SDLInputConnection` replica as correções como backspace mais texto novo.
  - `KeyboardMode.SendImeCode` → `SDLActivity.gmSetImeCode` → `InputMethodManager.restartInput`, sempre antes do `SDL_StartTextInput`.
- **Modo de edição de código:** com o teclado aberto no código:
  - A superfície da IDE ocupa só a faixa acima do teclado e da barra de símbolos.
  - Menu e barra de ferramentas ficam desligados. A lista certa é `StackPanel.StackedGadgets`; a anterior (`ChildGadgets`) só tinha o painel interno, e nada era escondido.
  - Os painéis recolhem. As sub-abas de evento e a barra de status ficam.
  - Se o editor ainda for uma janela do workspace, vira aba cheia sem perder o foco. O maximizar foca a janela nova e fechava o teclado; o foco transitório agora é ignorado e devolvido ao painel.
- **Barra de símbolos:** Tab, setas, `{ } ( ) [ ] ; = " _ . , + - * / < > ! & | : ? ' # $`, Desfazer e Refazer, rolável na horizontal, colada no teclado.
- **Campo de uma linha:** Enter confirma e fecha o teclado (`TextBox.OnInputComplete` com o campo ainda focado). Um toque no campo reabre.

## Seletor de arquivos

O `OpenFileDialog` devolvia sempre `List<string>`. O contrato do `OpenFileDialog_Linux` é lista para pasta ou seleção múltipla e `string` para um arquivo só. O erro quebrava, sem aviso, estas telas:
- Import Strip (Inspetor e editor de imagem).
- Importar som e fonte de arquivo.
- PNG para tilemap.
- Tema do editor.

E lançava `InvalidCastException` em Abrir/Importar projeto, importar `.yy`, campos de arquivo das Game Options e outros.

## Validação no aparelho (08/11/2026, REDMAGIC, Android 16)

Confirmado:
- **Import Strip** (menu Imagem do editor de imagem): o diálogo "Converter para quadros" abre inteiro e centralizado na desktop. A galinha virou 8 quadros de 16x16.
- **Diálogos:** a confirmação também abre centralizada, e o editor de sprite abre em 100% ocupando a área.
- **Campos de uma linha:** Enter confirma e fecha o teclado. Os quatro campos do diálogo foram preenchidos só com Enter.
- **Paleta de tiles:** um toque seleciona o tile. O lápis pinta com um toque e o balde preenche a sala.
- **Rolagem com um dedo:** acompanha o dedo na lista de Recursos e no editor de código.
- **Gaveta no workspace:** um toque no workspace recolhe os painéis, e tocar na borda reabre.
- **Código:**
  - Os eventos de objeto abrem direto numa aba cheia.
  - O primeiro toque já abre o teclado, em modo sem sugestões.
  - "else floor dir my anim" foi digitado sem nenhuma troca.
  - A barra de símbolos insere `( ) ;` e fica colada no teclado.
  - Menu, barra de ferramentas e faixas de abas somem: a área de código passou de 5 para 9 linhas visíveis.

Ajustes feitos durante o teste:
- O diálogo de imagem passava por `GUIBase.AddWindow`, então o gancho foi movido para lá (dialog-desktop-v2).
- A gaveta agora age só no workspace: no editor de sala os painéis são as ferramentas.
- A rolagem espera a sondagem quando o gesto é rápido.
- As barras de rolagem são procuradas em `ScrollPanel.ScrollBars` e em `Gadgets`.
- A barra de símbolos foi movida para `android.R.id.content`.

Ainda não confirmado no aparelho:
- Rolar até o cursor ao abrir o teclado. Agora roda em `MobileFrame`; antes dependia de `OnPostRender`, que não dispara com a tela parada.
- Toque longo armado: soltar abre o menu de contexto, e arrastar depois de segurar arrasta.
- Inércia ao soltar a rolagem e toque que só para a inércia.

## Pendências conhecidas

- O seletor do Android (DocumentsUI) segue a orientação do aparelho. Abre em retrato se o telefone estiver em pé.
- Validar no aparelho:
  - o fator de redução de diálogos grandes;
  - o comportamento das gavetas com o Inspetor durante a edição de sala;
  - a altura real da barra de símbolos em outros teclados.
