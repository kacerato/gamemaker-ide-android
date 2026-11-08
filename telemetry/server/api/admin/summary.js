// GET /api/admin/summary?days=30 — numeros do painel (Bearer ADMIN_TOKEN).
import { db } from "../../lib/db.js";
import { handle, reply, requireBearer } from "../../lib/http.js";

export default handle(async (req, res) => {
  requireBearer(req, process.env.ADMIN_TOKEN);
  const url = new URL(req.url, "http://x");
  const days = Math.min(Math.max(Number(url.searchParams.get("days")) || 30, 1), 180);
  const sql = db();
  const since = new Date(Date.now() - days * 864e5).toISOString();

  const [daily, totals, versions, groups, builds, buildErrors, devices, features] = await sql.transaction([
    sql`select day, sessions, installs, crashed_sessions, crashes, builds, builds_ok, boot_p50
        from gmide.daily where day >= ${since}::date order by day`,

    sql`select count(*)::int as sessions, count(distinct install_id)::int as installs,
          count(*) filter (where end_reason = 'crash')::int as crashed_sessions,
          round(extract(epoch from percentile_cont(0.5) within group (order by last_seen - started_at)))::int as session_p50_s,
          percentile_cont(0.5) within group (order by boot_ms)::int as boot_p50,
          percentile_cont(0.95) within group (order by boot_ms)::int as boot_p95,
          (select count(*)::int from gmide.crashes where ts >= ${since}) as crashes,
          (select count(distinct install_id)::int from gmide.crashes where ts >= ${since}) as crashed_installs
        from gmide.sessions where started_at >= ${since}`,

    sql`select coalesce(app_version, '?') as version, count(*)::int as sessions, count(distinct install_id)::int as installs,
          count(*) filter (where end_reason = 'crash')::int as crashed_sessions
        from gmide.sessions where started_at >= ${since} group by 1 order by 2 desc limit 12`,

    sql`select g.signature, g.process, g.kind, g.title, g.status, g.fixed_in, g.first_version, g.last_version,
          g.first_seen, g.last_seen,
          count(c.id)::int as hits, count(distinct c.install_id)::int as installs
        from gmide.crash_groups g join gmide.crashes c on c.signature = g.signature and c.ts >= ${since}
        group by g.signature order by hits desc limit 50`,

    sql`select target, count(*)::int as total, count(*) filter (where result = 'ok')::int as ok,
          count(*) filter (where result = 'gml_error')::int as gml_error,
          count(*) filter (where result in ('tool_crash', 'killed'))::int as crashed,
          percentile_cont(0.5) within group (order by duration_ms)::int as p50_ms,
          percentile_cont(0.95) within group (order by duration_ms)::int as p95_ms
        from gmide.builds where ts >= ${since} group by target order by total desc`,

    sql`select target, result, error_signature, min(left(error_text, 300)) as sample, count(*)::int as hits,
          count(distinct install_id)::int as installs, max(ts) as last_seen
        from gmide.builds where ts >= ${since} and result <> 'ok' and result <> 'cancelled'
        group by target, result, error_signature order by hits desc limit 30`,

    sql`select coalesce(device->>'model', '?') as model, device->>'api' as api, device->>'gpu' as gpu,
          (device->>'ram_mb')::int as ram_mb, count(*)::int as installs
        from gmide.installs where last_seen >= ${since} group by 1, 2, 3, 4 order by installs desc limit 20`,

    sql`select name, count(*)::int as hits, count(distinct install_id)::int as installs
        from gmide.events where ts >= ${since} group by name order by hits desc limit 30`,
  ]);

  reply(res, 200, { days, since, totals: totals[0], daily, versions, groups, builds, buildErrors, devices, features });
});
