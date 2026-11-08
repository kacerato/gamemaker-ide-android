# Telemetria de uso e crashes

Telemetria opcional para ver onde o app falha (IDE, builds, jogos) e como é usado,
para priorizar correções. Backend próprio: Vercel (projeto `gmide-telemetry`) + Neon
Postgres (projeto `gmide-telemetry`, schema `gmide`). Nenhum SDK de terceiros no app.

## Consentimento

Na primeira abertura, 8 s depois do início, aparece o diálogo "Ajudar a melhorar o GM IDE?"
(`TelemetryConsent.java`). A resposta fica em `files/telemetry/consent`:

| Arquivo | Efeito |
|---|---|
| ausente | grava na fila local, não envia (o primeiro boot conta se o usuário aceitar depois) |
| `1` | grava e envia |
| `0` | não grava; a fila e os ids são apagados |

Para mudar depois: barra de atalhos → `⋯` → "Dados de uso e falhas".

## O que é enviado

- **Instalação:** UUID aleatório (`files/telemetry/install_id`), modelo, fabricante, API,
  ABI, RAM, SoC e idioma do aparelho.
- **Sessão da IDE:** início, heartbeat a cada 5 min, fim com motivo (`normal`, `restart`, `crash`,
  `anr`, `oom`, `killed`, `unknown`), tempo de abertura até `IDE.Execute`.
- **Builds** (`Telemetry.Build`): alvo, resultado, duração, código de saída, versão do runtime
  e até 12 linhas de erro do Igor (com `error|exception|failed|fatal`).
- **Crashes** (`Telemetry.CrashRecord`): processo, tipo, pilha, mensagem e o final do logcat do processo.
- **Eventos:** `ide.install`, `game.run`, `export.apk|web|windows`, `build.process_exit`,
  `runner.console_error` e `unobserved_task`.

Antes de gravar, `Telemetry.Scrub` troca caminhos em `/storage/emulated/N/...` e `/sdcard/...`
por `<sdcard>/…/<arquivo>`, o diretório do app por `<app>` e e-mails por `<email>`. Não são enviados
projetos, código GML nem dados de conta. As linhas de erro de build podem citar nomes de
scripts/objetos do projeto (ex.: `gml_Object_obj_player_Step_0`).

## Onde cada coisa é capturada

| Processo | Fonte | Arquivo |
|---|---|---|
| todos | `AppDomain.UnhandledException` e `AndroidEnvironment.UnhandledExceptionRaiser` (exceções .NET e Java) | `host/Telemetry/Telemetry.cs` |
| IDE, na abertura seguinte | `ActivityManager.getHistoricalProcessExitReasons` (API 30+): crash nativo, ANR, falta de memória, motivo do fim da sessão anterior | `host/Telemetry/ExitReasons.cs` |
| IDE, crash nativo | linhas `gmhost-crash` do `Download/gmide-log.prev.txt` (LogMirror), conferidas pelo pid do cabeçalho; nos outros processos, cadeias legíveis do tombstone (API 31+) | `ExitReasons.NativeStack` |
| `:build` | build do Igor: início no `HandleClient`, fim por retorno, `Environment.Exit` (erros de GML) ou Stop | `host/Telemetry/BuildTelemetry.cs`, `BuildBridge.cs` |
| `:build` | jogo local (`RunnerActivity` em `:game`): `FATAL EXCEPTION` / `Fatal signal` no logcat do jogo | `LocalGameRun.cs` |
| IDE | Gerar APK / Web / Windows | `ApkExport.cs` |
| IDE (WebView) | runner GX: `Uncaught ...` no console vira crash `runner_js`; queda do renderer vira crash e fecha só a prévia | `GameRunnerActivity.java` |

Em "Run", a duração do build é a da compilação: termina quando o jogo abre (`BuildTelemetry.CompileDone`).
As compilações disparadas pelo "Gerar APK" não geram um build próprio no `:build`; o `ApkExport` registra o resultado.

Limite conhecido: falhas que a IDE detecta antes de chamar o Igor (ex.: "FAILED: Run Program
Complete" por configuração da plataforma, ver `run-preview-analise.md`) não passam pelo `:build` e
não aparecem como build.

## Fila e envio

Cada processo grava JSONL em `files/telemetry/q-<processo>-<pid>.jsonl`, abrindo e fechando o
arquivo a cada item. Só o processo da IDE envia (`TelemetryUploader`): 25 s depois da abertura,
depois a cada `flush_interval_s` (padrão 600 s, vem de `/api/v1/config`), com espera crescente
após falhas (até 1 h), e no fim normal da IDE.

1. `q-*.jsonl` → `s-<guid>.jsonl` (rename; o próximo item cai num `q-` novo);
2. `s-*` → lotes `b-<batch_id>.json` (até 400 itens / 384 KB);
3. POST gzip para `https://gmide-telemetry.vercel.app/api/v1/ingest`. 2xx ou 4xx definitivo apaga o
   lote; 408/429/5xx/rede mantém o mesmo `batch_id` para o próximo ciclo (o servidor descarta repetidos).

A fila local tem teto de 2 MB, e os arquivos mais antigos saem primeiro.

## Formato (`gmide.telemetry/1`)

```json
{
  "schema": "gmide.telemetry/1",
  "batch_id": "uuid", "install_id": "uuid",
  "app": { "version": "0.2.2", "code": 4, "runtime": "2026.0.0.23" },
  "device": { "model": "...", "manufacturer": "...", "api": 34, "abi": "arm64-v8a", "ram_mb": 7680, "gpu": "QTI SM8550", "locale": "pt-BR" },
  "items": [
    { "type": "session_start", "session_id": "uuid", "ts": 1794169000000, "boot_ms": 8200 },
    { "type": "session_heartbeat", "session_id": "uuid", "ts": 0 },
    { "type": "session_end", "session_id": "uuid", "ts": 0, "reason": "crash" },
    { "type": "event", "session_id": "uuid", "ts": 0, "name": "game.run", "props": { "result": "ok" } },
    { "type": "build", "session_id": "uuid", "ts": 0, "target": "run_android", "result": "gml_error", "duration_ms": 3400, "exit_code": 1, "runtime": "2026.0.0.23", "error": "..." },
    { "type": "crash", "session_id": "uuid", "ts": 0, "process": "build", "kind": "managed", "exception_type": "...", "message": "...", "stack": "...", "log_tail": "...", "meta": {} }
  ]
}
```

Valores aceitos: `target` ∈ `run_gx run_android apk web windows other`;
`result` ∈ `ok failed gml_error tool_crash killed cancelled`;
`process` ∈ `ide build game runner`;
`kind` ∈ `managed java native anr oom runner_js killed`.
O servidor ignora tipos de item desconhecidos, então o app pode ganhar tipos novos antes do servidor.

## Servidor (`telemetry/server`)

| Rota | Uso |
|---|---|
| `POST /api/v1/ingest` | lote do app (512 KB no máximo, gzip opcional, até 120 lotes/h por instalação) |
| `GET /api/v1/config` | `enabled` (env `TELEMETRY_DISABLED=1` desliga), `flush_interval_s`, `log_tail_bytes` |
| `GET /api/admin/summary?days=N` | números do painel (`Authorization: Bearer ADMIN_TOKEN`) |
| `GET/POST /api/admin/crash` | detalhe de um grupo de crash; marcar `open`/`fixed` (com versão)/`ignored` |
| `GET /api/cron/retention` | cron diário: apaga eventos, builds e crashes com mais de 90 dias (`CRON_SECRET`) |
| `/` | painel (pede o `ADMIN_TOKEN` uma vez e guarda no navegador) |

Os crashes são agrupados por uma assinatura calculada no servidor (`lib/batch.js`): processo,
tipo, exceção e os 6 primeiros quadros da pilha, sem endereços, offsets, linhas e caminhos.
Um grupo marcado como corrigido na versão X reabre sozinho se voltar numa versão ≥ X.

Variáveis do projeto Vercel: `DATABASE_URL`, `ADMIN_TOKEN` e `CRON_SECRET`, todas do tipo
sensitive. Os mesmos valores ficam em `telemetry/server/.env.local`, que está fora do Git.
O schema está em `telemetry/server/schema.sql`, que é idempotente.

```sh
cd telemetry/server
npm test                      # validacao do lote e assinatura de crash (node --test)
npx vercel link --project gmide-telemetry
npx vercel deploy --prod      # publica em https://gmide-telemetry.vercel.app
```

O projeto Vercel não está ligado ao GitHub: publique com o comando acima depois de `vercel login`.

## Consultas úteis (Neon)

```sql
-- sessões sem crash por versão, últimos 7 dias
select app_version, count(*) sessions,
       round(100.0 * count(*) filter (where end_reason is distinct from 'crash') / count(*), 1) crash_free
from gmide.sessions where started_at > now() - interval '7 days' group by 1 order by 1 desc;

-- builds que mais falham
select target, result, error_signature, count(*), min(left(error_text, 200))
from gmide.builds where result not in ('ok', 'cancelled') group by 1, 2, 3 order by 4 desc limit 20;
```
