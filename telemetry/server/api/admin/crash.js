// GET  /api/admin/crash?sig=<assinatura> — grupo + ultimas ocorrencias (pilha e final do log).
// POST /api/admin/crash  {signature, status: open|fixed|ignored, fixed_in?, note?}
import { db } from "../../lib/db.js";
import { handle, readJson, reply, requireBearer, HttpError } from "../../lib/http.js";

const Statuses = new Set(["open", "fixed", "ignored"]);

export default handle(async (req, res) => {
  requireBearer(req, process.env.ADMIN_TOKEN);
  const sql = db();

  if (req.method === "POST") {
    const body = await readJson(req, 16 * 1024);
    if (typeof body.signature !== "string" || !Statuses.has(body.status)) throw new HttpError(400, "campos");
    const fixedIn = body.status === "fixed" ? String(body.fixed_in || "").slice(0, 32) || null : null;
    const rows = await sql`update gmide.crash_groups set status = ${body.status}, fixed_in = ${fixedIn},
        note = coalesce(${body.note ?? null}, note)
      where signature = ${body.signature} returning signature, status, fixed_in, note`;
    if (!rows.length) throw new HttpError(404, "grupo");
    return reply(res, 200, rows[0]);
  }

  const sig = new URL(req.url, "http://x").searchParams.get("sig");
  if (!sig) throw new HttpError(400, "sig");
  const [group, recent, byVersion, byDevice] = await sql.transaction([
    sql`select * from gmide.crash_groups where signature = ${sig}`,
    sql`select id, ts, process, kind, exception_type, message, stack, log_tail, app_version, runtime_version,
          device_model, android_api, meta
        from gmide.crashes where signature = ${sig} order by ts desc limit 10`,
    sql`select app_version, count(*)::int as hits from gmide.crashes where signature = ${sig} group by 1 order by 2 desc`,
    sql`select device_model, android_api, count(*)::int as hits from gmide.crashes where signature = ${sig}
        group by 1, 2 order by 3 desc limit 10`,
  ]);
  if (!group.length) throw new HttpError(404, "grupo");
  reply(res, 200, { group: group[0], recent, byVersion, byDevice });
});
