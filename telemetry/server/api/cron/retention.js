// Cron diario (vercel.json): apaga dados brutos com mais de 90 dias. Os grupos de crash ficam.
import { db } from "../../lib/db.js";
import { handle, reply, requireBearer } from "../../lib/http.js";

export default handle(async (req, res) => {
  requireBearer(req, process.env.CRON_SECRET);
  const sql = db();
  const r = await sql.transaction([
    sql`delete from gmide.events where ts < now() - interval '90 days'`,
    sql`delete from gmide.builds where ts < now() - interval '90 days'`,
    sql`delete from gmide.crashes where ts < now() - interval '90 days'`,
    sql`delete from gmide.batches where received_at < now() - interval '7 days'`,
    sql`delete from gmide.sessions where started_at < now() - interval '180 days'`,
  ]);
  reply(res, 200, { ok: true, statements: r.length });
});
