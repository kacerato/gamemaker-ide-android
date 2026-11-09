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
| `MobileShell.cs` | Casca mobile-first: esconde a moldura de desktop, docks como gavetas, estado e menu para o Java | IDE |
| `MobileShell.java`, `ShellIcon.java` | Barra superior, trilho de painéis, folhas do menu ⋮ e de editores | UI |
| `MobileWindowGrip.cs`, `WindowGrip.java` | Alça de redimensionar a janela flutuante em foco; na casca, botão "No workspace" | IDE / UI |
| `KeyboardMode.cs` | Modo de edição de código com teclado | IDE |
| `IdeSetup.cs` | Foco → teclado; Enter fecha o teclado em campo de uma linha | IDE |
| `GmIdeActivity.java` | Superfície acima do teclado, barra de eco, barra de símbolos, vibração | UI |
| `SDLActivity.java` | Tipo de entrada do IME (sem sugestões; modo código) e `gmCommitText` | UI |
| `AndroidFileDialogs.cs` | Seletor de arquivos com o mesmo contrato do Linux | IDE |

## Casca mobile-first

Ampliar a escala não resolvia: a moldura de desktop (menu, barra de ferramentas, três docks fixos e uma faixa de abas em cada painel) ocupava metade da tela em paisagem. A casca troca essa moldura por views Android e deixa o editor com a tela inteira. Nenhuma função some: tudo que estava na moldura continua acessível.

**É uma alternativa, não o padrão.** A interface desktop continua sendo a padrão, e as duas seguem recebendo melhorias. A casca é ligada no menu "⋯" do botão de atalhos ("Interface para celular"), e o fim do ⋮ da casca tem "Interface clássica (desktop)". A escolha fica gravada em `gmide/mobile_shell` (padrão `false`) e é aplicada ao vivo, sem reiniciar.

```
+-----------------------------------------------------------------------+
| Projeto / editor v   [Game Options] [Exportar] [> Executar] Dep. [] : |  barra (48dp)
+--------+--------------------------------------------------------------+
|Recursos|                                                              |
|Inspetor|                                                              |
| ------ |        editor / workspace com a tela inteira                 |
| Saida  |   (um painel do trilho abre como gaveta, empurrando)         |
| Erros  |                                                              |
| Busca  |                                                              |
+--------+--------------------------------------------------------------+
 trilho (64dp)
```

- **Moldura escondida, não destruída.** `MobileShell.Apply` desliga com `RequestDisabled` os `StackedGadgets` da desktop que não contêm a grade dos docks (as linhas `ide_menu` e `ide_toolbar`). Também zera as linhas da faixa de abas (`TabControl.GridRowLayout`, menos `ClientPanelGridCell`) do workspace e dos três docks, e fecha os docks: `DockContainer.Collapse()` (o caminho da IDE, que desliga os painéis e o divisor) mais a coluna ou linha do dock na grade em 0 px, para a tirinha "«" do dock recolhido sumir. O `DockContainer.Hide()` foi tentado e descartado: no dock de baixo o conteúdo (o Prefab Library, por exemplo) continuava desenhado por cima do workspace e pegava os toques. A pinça caía nele em vez de dar zoom no workspace. A cada 30 quadros a casca confere se algum layout refeito trouxe a moldura de volta.
- **Barra superior.** Mostra o nome do projeto (`ProjectManager.Current.name`) e o editor ativo (aba ativa do workspace). Um toque abre o seletor de editores. Também tem o progresso do build (`DesktopChrome.ProgressLabel`), os botões com ícone e rótulo "Game Options" (no teste, achar o Game Options dentro do menu foi difícil) e "Exportar" (a tela do host com Android APK, Web/HTML5 e Windows/EXE, o mesmo comando `apk` do botão de atalhos), e os botões Executar, Depurar e Parar: o toque chama `Button.ClickButton()` em `ide_toolbar_play`, `ide_toolbar_debug` e `ide_toolbar_stop`, e cada botão fica apagado quando o original está `Deactivated`. Por fim, o ⋮.
- **Menu ⋮.** Tem duas seções:
  - "Ações rápidas" começa com "Exportar jogo (APK, Web, Windows)" e reúne todos os botões das linhas escondidas da moldura, cada um com ícone e o nome da dica da IDE (`ToolTipCaption`, ou `Language.GetString` da chave). São Game Options, o alvo de compilação ("Alvo: Test | GMS2 VM | Default | Default", montado com os rótulos `target_select_label_*`), Salvar, Limpar, Criar executável, Novo/Abrir/Fechar projeto, Ajuda, zoom e conta. Executar, Depurar e Parar ficam de fora porque já estão na barra.
  - "Menus" traz o menu original, nível a nível: as entradas do `MasterMenuBar` e, se for separado, do menu do módulo.
  - Antes de listar, a casca faz o que a IDE faz ao abrir um menu: `OnActiveStateChange` (os rótulos se atualizam) e `UpdateLayoutAndRetrieveAllLeafEntries` (some o que tem feature flag desligada).
  - Submenus preguiçosos (`LazyMenu`, `LazySubMenu`: projetos recentes, layouts...) são criados uma vez por abertura do ⋮.
  - O toque num item chama `ClickButton()`, o mesmo caminho do Enter no menu da IDE. Itens com caixa alternam `CheckBox.Checked`, como o `MouseUp` original.
  - Linhas de 52dp, itens desativados apagados, voltar pelo cabeçalho ou pelo botão voltar do Android.
- **Seletor de editores** (substitui as faixas de abas): lista as abas do workspace (tocar ativa, ✕ fecha), "Novo workspace" (o mesmo `TabControl_AddButtonClicked` do "+") e as janelas de editor do workspace atual (tocar foca e leva a vista até ela).
- **Trilho de painéis.** Tem um botão por aba de dock. Os painéis conhecidos ganham ícone próprio e um rótulo curto em português que cabe nos 64dp: Recursos, Inspetor, Saída, Erros, Busca, Versões (controle de versão), Pausas (breakpoints), Prefabs, Camadas. O nome original fica na descrição de acessibilidade e no cabeçalho do painel. Os painéis laterais vêm primeiro e os do dock de baixo depois de um divisor. Um toque abre aquele painel como gaveta (`Expand` + `ActivateTab`) e fecha as outras. Tocar de novo, tocar no workspace ou usar o botão voltar do Android fecha. O SDL prende o voltar (`SDL_ANDROID_TRAP_BACK_BUTTON`, vira Esc na IDE), então `GmIdeActivity.dispatchKeyEvent` intercepta antes, só quando a casca tem folha ou gaveta aberta. Abrir um editor (aba ativa do workspace muda) fecha a gaveta de Recursos, porque foi ela que abriu o editor; os painéis que o próprio editor abre ficam. A gaveta ocupa no máximo 45% da largura (laterais) ou da altura (a de baixo): o tamanho salvo no layout chegava a cobrir a tela, e aí não havia onde tocar fora nem dar pinça no workspace. O trilho rola até o painel aberto. No editor de sala a gaveta fica aberta enquanto se edita, porque nele os painéis são as ferramentas.
- **Inspetor segue a seleção.** Selecionar algo num editor (uma instância na sala, por exemplo) dispara `InspectorManager.OnInspectorSelected`, e a casca abre a gaveta do dock que contém o `InspectorScrollPanel`. Seleções feitas dentro de um dock (Recursos, Camadas) não trocam a gaveta. Abrir a sala, que inspeciona o próprio `GMRoom`, também não.
- **Modo foco.** Com o teclado aberto no editor de código (`KeyboardMode`), a barra e o trilho saem e o código fica com a altura toda.
- **Interface clássica.** Ao voltar, a moldura reaparece e os docks com abas voltam recolhidos (`Collapse`), com a tirinha de expandir.
- **Layout salvo.** Logo depois de abrir o projeto, a IDE restaura o layout salvo e expande ou recolhe os docks de novo. Até o usuário mexer num painel, ou por 15 s, a casca fecha esses docks outra vez. Depois disso, um dock "recolhido" pela IDE (com a tirinha) volta a ficar fechado sem ela.
- **Canal.** Java → C#: comandos `shell:...` pela fila da barra de atalhos (`ShortcutBar.command`, tecla HELP, `ShortcutBridge`), aplicados na thread da IDE em `MobileFrame`. C# → Java: `MobileShell.onState(json)`, só quando o estado muda, e `MobileShell.onMenu(caminho, json)`.

### Validação da casca (08/11/2026, REDMAGIC, Android 16)

Confirmado no aparelho:
- A moldura de desktop some e os três docks fecham, mesmo depois que a IDE restaura o layout salvo.
- A barra mostra o projeto e o editor ativo ("Workspace 1", "rm_level_1").
- O ⋮ abre o menu original. Funcionam Preferences, Game Options e o seletor de alvo (Platform/Output/Device/Config), mas o popup do alvo fica ancorado onde o botão escondido estava e encosta na borda direita.
- "Exportar" abre a tela Android APK / Web ZIP / Windows EXE.
- O seletor de editores lista as abas e "Novo workspace".
- As gavetas Recursos, Inspetor, Saída, Erros e Prefabs abrem e fecham pelo trilho. A de baixo fica limitada a ~45% da altura e, ao fechar, não deixa conteúdo por cima do workspace.
- O caso da hornet: com a gaveta fechada, tocar numa instância da sala abre o Inspetor com ela (`GMRSequenceGraphic`), e a gaveta de Recursos fecha.
- O voltar do Android fecha a gaveta aberta sem mandar Esc para a IDE. Sem gaveta nem folha aberta, continua virando Esc.
- Abrir `rm_level_1` pela gaveta de Recursos fecha essa gaveta. As Camadas e o Inspetor que a sala abre ficam.
- A pinça no workspace funciona com a casca ligada: escala de 0,46 a 0,14 (gestos em `TouchInput.cs`, de outra sessão).
- A interface desktop é a padrão. A casca liga e desliga ao vivo pelo menu "⋯" do botão de atalhos.

Ainda não confirmado:
- Alça de redimensionar e "No workspace" (`MobileWindowGrip`): não apareceram nos testes. O "Prefab Library" que parecia flutuante era o conteúdo do dock de baixo vazando depois do `Hide()` (corrigido), não uma janela flutuante. Falta testar com uma janela flutuante de verdade (Preferências, `FloatingDock`).
- Mouse Bluetooth (arrastar com o direito na room) com a casca ligada: validado pela sessão de gestos só sem a casca.
- A primeira chamada JNI da casca às vezes falha ("Handle value is not valid"). O estado é reenviado no ciclo seguinte e a barra aparece normalmente.

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
- **Dois dedos juntos num canvas** (room, sprite, tileset, workspace): o conteúdo acompanha os dedos 1:1 nos dois eixos, inclusive para os lados.
  - Quando o gesto vira arrasto, a IDE diz no quadro seguinte o que há sob o centro dos dedos (`MobileWindowFit.ProbeCanvas`). Em um `PanArea` (como `RoomEditor_Room`) ou no `WorkspaceArea`, o deslocamento muda o `Scroll` do alvo pelo mesmo caminho do arraste com o botão do meio (`MobileWindowFit.ApplyPending`).
  - Em listas, no Inspetor e no código os dois dedos continuam virando roda do mouse.
  - Antes, tudo virava roda em notches de 24 dp. Além disso, o `SDL_MULTIGESTURE` nativo chegava ao `PanArea`/`WorkspaceArea`. Qualquer variação de distância entre os dedos acima de 0,03 virava zoom e travava o zoom (`SetZoomLockGadget`). Com ele travado, `DoMouseWheelEvent` descartava toda roda, e arrastar a room para os lados dava zoom tremido ou nada. O filtro agora descarta o `SDL_MULTIGESTURE`, e só a nossa pinça (Ctrl+roda) dá zoom.
- **Pinça no workspace**, inclusive com os dedos sobre uma janela de editor: zoom contínuo, proporcional à abertura dos dedos e ancorado no ponto entre eles. Mover os dedos juntos também arrasta.
  - A mesma sondagem decide o alvo. Se, subindo a partir do gadget sob os dedos, o primeiro alvo for o `WorkspaceArea` e não um `PanArea`, a escala vira a do início do gesto vezes a razão entre as distâncias dos dedos. A conta é a do `WorkspaceArea.WorkspaceZoom`, que é protegido, dentro de `MinScale`..`MaxScale` (0,1 a 20).
  - Antes, a pinça virava Ctrl+roda, e a janela sob os dedos a consumia. `ScrollBar.Parent_MouseWheel` só deixa o Ctrl+roda subir em janelas de uma cadeia do workspace, e listas como a do Prefabs usam o Ctrl+roda para o tamanho dos ícones.
  - Abrir um editor não volta mais o workspace para 100%: a vista só anda até ele (`MobileWindowPolicy.ShowWindow`).
- **Pinça em canvas e painéis** (room, sprite, Asset Browser): Ctrl+roda no ponto entre os dedos.
- **Mouse real (Bluetooth/USB) sem teclado:** na IDE desktop, a vista de um canvas anda com o botão do meio, com Espaço+esquerdo ou com Shift+roda, e o zoom é Ctrl+roda. No celular não há teclado, e muitos mouses só têm roda vertical: dava para rolar a room só para cima e para baixo. Sobre canvas e workspace (`TouchInput.HandleRealMouse`):
  - arrastar com o botão direito move a vista 1:1 em qualquer direção, pelo mesmo caminho dos dois dedos;
  - segurar o direito e girar a roda dá zoom (Ctrl+roda);
  - clique direito sem arrastar continua abrindo o menu de contexto.
  - O "down" do direito fica segurado até a sondagem dizer o que há sob o ponteiro. Fora de canvas (listas, painéis) ele é entregue e o arraste com o direito segue como na IDE desktop. A roda sozinha e o botão do meio não mudaram.
- **Modo Mover** (barra de atalhos): um dedo arrasta o canvas sob ele (por exemplo, a room) ou, fora de um canvas, o workspace. A pinça dá zoom no mesmo alvo.

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
5. **Redimensionar pelo toque.** A borda de redimensionar da IDE tem poucos pixels. Com uma janela flutuante redimensionável em foco (Prefab Library, Preferências...), uma alça verde de 48dp aparece no canto inferior direito dela (`WindowGrip.java`). Ela acompanha o dedo e o deslocamento vira `Width`/`Height` em unidades (`MobileWindowGrip.Resize`), com mínimo de 220x140 e máximo até a borda da tela. Vale nos dois modos.
6. **Levar para o workspace** (só na casca). Ao lado dos botões da barra de título da janela flutuante em foco aparece "No workspace". O toque tira a janela da desktop (`WindowInterface.RemoveWindow`), retira da política de janelas flutuantes e a põe numa corrente do workspace atual (`WindowManager.AddChainRoot(janela, null)`, o mesmo caminho dos editores). Lá ela acompanha o zoom e o arraste do workspace. Diálogos (`*Dialog`) ficam de fora, porque têm dono e fecham com ele.

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

Confirmado em 08/11/2026 (POCO F7, editor de sala do Tower Defense Template, `rm_level_1`):
- **Dois dedos na room:** a sondagem respondeu `RoomEditor_Room` em cerca de 15 ms e a room acompanhou os dedos nos dois eixos, sem notches de roda. A pinça deu zoom nos dois sentidos.
- **Pinça no workspace com o Prefabs aberto:** 34 gestos de dois dedos foram aplicados direto no workspace, 22 deles com os dedos sobre janelas (`TitleBar`, `ScrollPanel`, `BrowseButton`). A escala variou de 0,10 a 1,35, sem erros.
- **Mouse Bluetooth na room:** 8 de 8 arrastos com o direito moveram a vista, inclusive os rápidos. Na primeira versão, a sondagem exigia o ponteiro a 3 px do ponto do clique, e um arraste rápido virava clique direito comum. Para o mouse, agora vale o que estiver sob o ponteiro no quadro seguinte (`CanvasProbeTolerance`), com até 400 ms de espera. O zoom com o direito segurado + roda ainda não foi testado no aparelho.
- **Modo Mover na room:** um arraste de 400 px moveu a vista cerca de 395 px para a esquerda, sem desvio vertical. O arraste na diagonal também funcionou.
- O `adb` não injeta dois dedos nesse aparelho, porque o SELinux bloqueia o `sendevent` no touchscreen. Por isso o gesto de dois dedos foi testado à mão, e o caminho comum (`ApplyPending` → `PanArea.Scroll`) foi testado pelo modo Mover com `input swipe`.

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
