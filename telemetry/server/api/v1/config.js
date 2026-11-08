// GET /api/v1/config — chave remota: desliga o envio ou muda o intervalo sem nova versao do app.
import { handle, reply } from "../../lib/http.js";

export default handle(async (_req, res) => {
  reply(res, 200, {
    enabled: process.env.TELEMETRY_DISABLED !== "1",
    flush_interval_s: Number(process.env.FLUSH_INTERVAL_S || 600),
    log_tail_bytes: Number(process.env.LOG_TAIL_BYTES || 32768),
  });
});
