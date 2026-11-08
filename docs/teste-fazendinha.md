# Teste: jogo de fazenda feito na IDE do celular (08/11/2026)

Joguinho de fazenda criado do zero na IDE do aparelho (REDMAGIC, Android 16), tudo por toque/teclado virtual via adb e gravado em tela. O resultado: o personagem anda por toque e, tocando nele, você ara, planta e colhe trigo, com contador de trigo. Compilou e rodou pelo F5 no próprio celular (build de ~50 s).

- Vídeo de 1:30: `demo/rec/fazendinha-1m30.mp4` (12 cenas aceleradas de 10x a 40x; o jogo aparece em 10x)
- Gravação bruta: `demo/rec/seg_*.mp4` (51 min), com a linha do tempo em `demo/tools/marks.txt`
- Código GML usado: `demo/gml/`
- Projeto no aparelho: `Android/data/io.gmide.android/files/home/GameMakerProjects/Blank Pixel Game`
- Assets: [Sprout Lands Basic Pack](https://cupnooble.itch.io/sprout-lands-asset-pack), de Cup Nooble (só uso não comercial, crédito obrigatório, sem redistribuição). Foram copiados para `/sdcard/Download/Fazendinha` no aparelho.
- Referências de tutorial: as séries "Farming RPG Tutorial: GMS2" (FriendlyCosmonaut) e "GameMaker: Farming RPG" (Mager Dev) no YouTube. Só usei os temas dos episódios (movimento, inventário, plantio por estágios); não assisti os vídeos.

## Como o jogo foi montado

1. **Projeto:** modelo Blank Pixel Game.
2. **Sprites:**
   - `spr_jogador`: folha 192x192 importada e convertida em 16 quadros de 48 px (Imagem > Converter para quadros).
   - `spr_plantas`: 12 quadros de 16 px.
   - `spr_grama` e `spr_terra`: folhas de tiles.
3. **Tilesets:** `ts_grama` e `ts_terra`, com blocos de 16x16.
4. **`obj_planta`:**
   - Create: estado inicial (fase 0).
   - Step: cresce em 3 estágios.
5. **`obj_jogador`:**
   - Create: variáveis e preenchimento da grama.
   - Step: movimento por teclado e toque; arar, plantar e colher no tile da frente.
   - Draw GUI: contador.
6. **Room1:**
   - Tamanho 384x176.
   - Camadas Instances, Terra (tiles), Fundo (tiles) e Background.
   - Jogador colocado arrastando do Navegador de recursos.

## Problemas encontrados (por prioridade)

### Alta prioridade

**1. A paleta de tiles do editor de sala não seleciona tile com toque**
- **O que acontece:**
  - Em Editor de camada de tiles > Tiles, tentei toque simples, toque com arrasto curto e toque duplo. Nenhum seleciona o tile (a pré-visualização continua vazia), e o balde e o lápis não pintam.
  - O log mostra o foco chegando em `RoomEditor_TileDisplay`, ou seja, o toque chega no gadget.
- **Por que importa:** sem isso não dá para desenhar mapas, que é essencial num jogo de fazenda.
- **Contorno usado:** preencher a camada `Fundo` por código (`tilemap_set`, no Create do jogador).
- **Suspeita:** a seleção espera um arrasto com o botão pressionado entre MouseDown e MouseUp (seleção retangular de tiles), ou um movimento mínimo que o toque não gera. Investigar `RoomEditor_TileDisplay` / `TileSetSelector` com REA antes de mexer.

**2. O teclado (Gboard) autocorrige o código**
- **Trocas observadas** (foi preciso reescrever o Step inteiro):
  - `else` virou `eles`, `floor` virou `flor`, `dir` virou `Dir`.
  - `my` virou `ou`, `tm` virou `tem`, `gy` virou `Gy`, `p` virou `o`, `anim` virou `Anim`.
- **Causa:** `SDLActivity.onCreateInputConnection` declara `TYPE_CLASS_TEXT | TYPE_TEXT_FLAG_MULTI_LINE`, sem `TYPE_TEXT_FLAG_NO_SUGGESTIONS`. O IME corrige e capitaliza.
- **Sugestão:** pelo menos quando o foco estiver no editor de código, usar `TYPE_TEXT_FLAG_NO_SUGGESTIONS`, mais `TYPE_TEXT_VARIATION_VISIBLE_PASSWORD` se o Gboard ignorar a flag. Isso exige reiniciar o input (`InputMethodManager.restartInput`) quando o foco muda entre campo comum e código.
- **Contorno no teste:** nomes com `_`, que o Gboard não troca, e conferir cada arquivo `.gml` depois de salvar.

**3. "Importar imagem da faixa" (Import Strip) não abre o diálogo**
- **O que acontece:** tanto pelo botão do Inspetor quanto pelo menu Imagem do editor, o seletor do Android abre e devolve o arquivo. O log mostra `OpenFileDialog -> .../projects/_imported/personagem.png`, mas o diálogo de faixa nunca aparece e nada é importado.
- **O que funciona:** o "Importar" comum, no mesmo fluxo.
- **Contorno:** Importar mais Converter para quadros.
- **Suspeita:** o diálogo de faixa é aberto num callback que o `AndroidOpenFileDialog` síncrono não dispara, ou abre e fecha sem frame.

**4. Diálogos modais maiores que a tela e sem rolagem**
- **O que acontece:**
  - "Converter para quadros" abriu cortado à esquerda e mais alto que a tela.
  - A rolagem com um dedo não funciona dentro dele, e Enter não confirma.
  - O botão Converter ficou inacessível até eu baixar a escala da UI pelo menu ☰.
- **Sugestão:** aplicar aos diálogos modais o mesmo ajuste do Game Options flutuante (`MobileWindowFit`): limitar à área visível, centralizar e habilitar a rolagem.

### Média prioridade

**5. Janelas do workspace abrem fora da tela**
- **O que acontece:**
  - O editor de código do evento recém-criado abriu à direita, fora da área visível. Precisei do modo "Mover" para trazê-lo.
  - A janela do sprite também passa da borda direita (os controles de Origem ficaram inacessíveis).
- **Sugestão:** ao criar ou abrir uma janela no workspace, deslocar a câmera até ela, ou abrir editores direto em aba.

**6. O primeiro toque no editor de código não dá foco**
- **O que acontece:** dentro de um editor de código no workspace, o primeiro toque transformou a janela em aba própria, sem cursor. Só o segundo toque abriu o teclado.
- **Consequência:** como o primeiro toque não foca, o texto digitado logo depois se perde.

**7. O teclado continua aberto depois do Enter em campos de uma linha**
- **O que acontece:** depois de Enter num campo numérico, o teclado fica e cobre metade da tela. O toque seguinte no próximo campo caiu na engrenagem do Gboard e abriu as configurações dele.
- **Sugestão:** fechar o teclado no Enter de campos de uma linha (o eco já mostra o valor).

**8. A rolagem com um dedo no Inspetor anda pouco**
- **O que acontece:** no Inspetor da sala, cada gesto rola só alguns pixels. Foram necessários 3 ou 4 gestos para chegar à Altura.
- **Sugestão:** aumentar o passo da roda ou usar rolagem proporcional ao arrasto.

**9. Pressão longa no Navegador de recursos vira clique direito**
- **O que acontece:** segurar cerca de 0,6 s antes de arrastar um objeto para a sala abriu o menu de contexto em vez de arrastar.
- **O que funciona:** arrastar logo após o toque.
- **Sugestão:** cancelar o clique direito por pressão longa se houver movimento acima do limiar antes do tempo, ou dar prioridade ao drag.

**10. O seletor de arquivos do Android abre em retrato**
- **O que acontece:** o DocumentsUI gira a tela para retrato e a IDE volta em paisagem ao fechar. Funciona, mas é desconfortável.
- **Sugestões:**
  - Abrir o seletor com a orientação travada, se possível.
  - Lembrar a última pasta via `EXTRA_INITIAL_URI`.
- **Observação:** arquivos copiados via adb só aparecem depois do media scan. Isso não é problema da IDE.

### Funcionou bem

- **Navegação e criação:** tela inicial, Novo projeto e modelos; Criar recurso, com renomeação pelo Inspetor e barra de eco.
- **Editor de imagem:** menu Imagem, Importar e Converter para quadros, que lembra os últimos valores.
- **Objetos:** sprite do objeto e eventos (Criar, Etapa, Desenhar GUI). O diálogo GML Code / GML Visual também funciona.
- **Editor de código:** a digitação via teclado é correta, com indentação automática e sem chaves duplicadas. Ctrl+S salva.
- **Editor de sala:** largura e altura, camadas de tiles com escolha de tileset, e arrastar instância para a sala.
- **Jogo:**
  - F5 compila e abre o jogo no processo `:game`.
  - Os toques do jogo funcionam como mouse.
  - O menu ☰ do jogo (Parar) volta à IDE com "SUCCESS: Run Program Complete".
- **Escala da UI:** o menu ☰ (Escala da UI + e −) ajusta a escala na hora.
