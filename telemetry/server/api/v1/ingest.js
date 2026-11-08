// POST /api/v1/ingest — lote gmide.telemetry/1 do app (gzip opcional).
// Ver docs/telemetria.md para o formato e lib/batch.js para a validacao.
import { db } from "../../lib/db.js";
import { handle, readJson, reply, HttpError } from "../../lib/http.js";
import { parseBatch, BatchError } from "../../lib/batch.js";

const MaxBodyBytes = 512 * 1024;
const MaxBatchesPerHour = 120;

export default handle(async (req, res) => {
  if (req.method !== "POST") throw new HttpError(405, "metodo");
  if (process.env.TELEMETRY_DISABLED === "1") return reply(res, 503, { error: "desligado" });

  let batch;
  try { batch = parseBatch(await readJson(req, MaxBodyBytes)); }
  catch (e) { if (e instanceof BatchError) throw new HttpError(400, e.message); throw e; }

  const sql = db();
  const items = batch.sessions.length + batch.heartbeats.length + batch.ends.length +
    batch.events.length + batch.builds.length + batch.crashes.length;

  const [gate] = await sql`
    with recent as (
      select count(*)::int as n from gmide.batches
      where install_id = ${batch.installId} and received_at > now() - interval '1 hour'
    ), ins as (
      insert into gmide.batches (id, install_id, items)
      select ${batch.id}, ${batch.installId}, ${items} from recent where n < ${MaxBatchesPerHour}
      on conflict (id) do nothing
      returning id
    )
    select (select n from recent) as recent, (select count(*)::int from ins) as inserted`;
  if (gate.recent >= MaxBatchesPerHour) throw new HttpError(429, "limite");
  if (gate.inserted === 0) return reply(res, 200, { ok: true, duplicate: true });

  const app = batch.app, dev = batch.device;
  const json = (rows) => JSON.stringify(rows);
  try {
    await sql.transaction([
      sql`insert into gmide.installs (id, app_version, device)
          values (${batch.installId}, ${app.version}, ${json(dev)}::jsonb)
          on conflict (id) do update set last_seen = now(), app_version = excluded.app_version, device = excluded.device`,

      sql`insert into gmide.sessions (id, install_id, started_at, last_seen, app_version, app_code, runtime_version, device_model, android_api, boot_ms)
          select x.id, ${batch.installId}, x.ts, x.ts, ${app.version}, ${app.code}, ${app.runtime}, ${dev.model}, ${dev.api}, x."bootMs"
          from jsonb_to_recordset(${json(batch.sessions)}::jsonb) as x(id uuid, ts timestamptz, "bootMs" int)
          on conflict (id) do update set boot_ms = coalesce(excluded.boot_ms, gmide.sessions.boot_ms)`,

      sql`update gmide.sessions s set last_seen = greatest(s.last_seen, x.ts)
          from (select id, max(ts) as ts from jsonb_to_recordset(${json(batch.heartbeats)}::jsonb) as h(id uuid, ts timestamptz) group by id) x
          where s.id = x.id and s.install_id = ${batch.installId}`,

      sql`update gmide.sessions s set ended_at = x.ts, last_seen = greatest(s.last_seen, x.ts),
            end_reason = case when s.end_reason = 'crash' then 'crash' else x.reason end
          from jsonb_to_recordset(${json(batch.ends)}::jsonb) as x(id uuid, ts timestamptz, reason text)
          where s.id = x.id and s.install_id = ${batch.installId}`,

      sql`insert into gmide.events (install_id, session_id, ts, name, app_version, props)
          select ${batch.installId}, x."sessionId", x.ts, x.name, ${app.version}, x.props
          from jsonb_to_recordset(${json(batch.events)}::jsonb) as x("sessionId" uuid, ts timestamptz, name text, props jsonb)`,

      sql`insert into gmide.builds (install_id, session_id, ts, target, result, duration_ms, exit_code, runtime_version, app_version, error_signature, error_text)
          select ${batch.installId}, x."sessionId", x.ts, x.target, x.result, x."durationMs", x."exitCode", x.runtime, ${app.version}, x."errorSignature", x."errorText"
          from jsonb_to_recordset(${json(batch.builds)}::jsonb) as x("sessionId" uuid, ts timestamptz, target text, result text,
            "durationMs" int, "exitCode" int, runtime text, "errorSignature" text, "errorText" text)`,

      // Grupo de crash: conta, atualiza a ultima versao e reabre se voltou numa versao >= fixed_in.
      sql`insert into gmide.crash_groups as g (signature, process, kind, title, first_version, last_version, occurrences)
          select signature, min(process), min(kind), min(title), ${app.version}, ${app.version}, count(*)
          from jsonb_to_recordset(${json(batch.crashes)}::jsonb) as x(signature text, process text, kind text, title text)
          group by signature
          on conflict (signature) do update set
            last_seen = now(), occurrences = g.occurrences + excluded.occurrences, last_version = excluded.last_version,
            status = case when g.status = 'fixed' and g.fixed_in ~ '^[0-9]+([.][0-9]+)*$' and excluded.last_version ~ '^[0-9]+([.][0-9]+)*$'
                          and string_to_array(excluded.last_version, '.')::int[] >= string_to_array(g.fixed_in, '.')::int[]
                     then 'open' else g.status end`,

      sql`insert into gmide.crashes (install_id, session_id, ts, signature, process, kind, exception_type, message, stack, log_tail,
            app_version, runtime_version, device_model, android_api, meta)
          select ${batch.installId}, x."sessionId", x.ts, x.signature, x.process, x.kind, x.type, x.message, x.stack, x."logTail",
            ${app.version}, ${app.runtime}, ${dev.model}, ${dev.api}, x.meta
          from jsonb_to_recordset(${json(batch.crashes)}::jsonb) as x("sessionId" uuid, ts timestamptz, signature text, process text,
            kind text, type text, message text, stack text, "logTail" text, meta jsonb)`,

      sql`update gmide.sessions s set end_reason = 'crash'
          from jsonb_to_recordset(${json(batch.crashes)}::jsonb) as x("sessionId" uuid, process text)
          where s.id = x."sessionId" and x.process = 'ide' and s.install_id = ${batch.installId}`,
    ]);
  } catch (e) {
    // Libera o id para o reenvio do app nao ser tratado como duplicado.
    await sql`delete from gmide.batches where id = ${batch.id}`.catch(() => {});
    throw e;
  }
  return reply(res, 200, { ok: true, items });
});
