// Validacao do lote gmide.telemetry/1 e assinatura de crash. Sem dependencias,
// para rodar igual nos testes (node --test) e na funcao da Vercel.
import { createHash } from "node:crypto";

export const Schema = "gmide.telemetry/1";
export const MaxItems = 500;

const Uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const Name = /^[a-z][a-z0-9_.]{0,63}$/;
const Processes = new Set(["ide", "build", "game", "runner", "restart"]);
const Kinds = new Set(["managed", "java", "native", "anr", "oom", "runner_js", "killed"]);
const Targets = new Set(["run_gx", "run_android", "apk", "web", "windows", "other"]);
const Results = new Set(["ok", "failed", "gml_error", "tool_crash", "killed", "cancelled"]);
const EndReasons = new Set(["normal", "crash", "killed", "restart", "anr", "oom", "unknown"]);

export class BatchError extends Error {}

const str = (v, max) => (typeof v === "string" ? v.slice(0, max) : v == null ? null : String(v).slice(0, max));
const int = (v) => (Number.isFinite(v) ? Math.trunc(v) : null);
const uuid = (v, field) => {
  if (typeof v !== "string" || !Uuid.test(v)) throw new BatchError(`${field} invalido`);
  return v.toLowerCase();
};
const optUuid = (v, field) => (v == null ? null : uuid(v, field));
const oneOf = (set, v, field) => {
  if (!set.has(v)) throw new BatchError(`${field} invalido: ${String(v).slice(0, 40)}`);
  return v;
};

// O relogio do aparelho pode estar errado: aceita ate 30 dias para tras e 1 dia para frente.
function ts(v, now) {
  const t = typeof v === "number" ? v : Date.parse(v);
  if (!Number.isFinite(t)) throw new BatchError("ts invalido");
  if (t < now - 30 * 864e5 || t > now + 864e5) return new Date(now).toISOString();
  return new Date(t).toISOString();
}

function props(v) {
  if (v == null) return {};
  if (typeof v !== "object" || Array.isArray(v)) throw new BatchError("props invalido");
  const out = {};
  for (const [k, val] of Object.entries(v).slice(0, 32)) {
    if (!Name.test(k)) continue;
    if (typeof val === "number" || typeof val === "boolean") out[k] = val;
    else if (val != null) out[k] = str(val, 256);
  }
  return out;
}

export function parseBatch(body, now = Date.now()) {
  if (!body || typeof body !== "object") throw new BatchError("corpo invalido");
  if (body.schema !== Schema) throw new BatchError("schema");
  const app = body.app || {}, device = body.device || {};
  const batch = {
    id: uuid(body.batch_id, "batch_id"),
    installId: uuid(body.install_id, "install_id"),
    app: { version: str(app.version, 32), code: int(app.code), runtime: str(app.runtime, 48) },
    device: {
      model: str(device.model, 64), manufacturer: str(device.manufacturer, 64),
      api: int(device.api), abi: str(device.abi, 24), ram_mb: int(device.ram_mb),
      gpu: str(device.gpu, 96), locale: str(device.locale, 16),
    },
    sessions: [], heartbeats: [], ends: [], events: [], builds: [], crashes: [],
  };
  const items = Array.isArray(body.items) ? body.items : [];
  if (items.length > MaxItems) throw new BatchError("itens demais");
  for (const it of items) {
    if (!it || typeof it !== "object") continue;
    const sessionId = optUuid(it.session_id, "session_id");
    const at = ts(it.ts, now);
    switch (it.type) {
      case "session_start":
        if (!sessionId) throw new BatchError("session_start sem session_id");
        batch.sessions.push({ id: sessionId, ts: at, bootMs: int(it.boot_ms) });
        break;
      case "session_heartbeat":
        if (sessionId) batch.heartbeats.push({ id: sessionId, ts: at });
        break;
      case "session_end":
        if (!sessionId) throw new BatchError("session_end sem session_id");
        batch.ends.push({ id: sessionId, ts: at, reason: oneOf(EndReasons, it.reason ?? "unknown", "reason") });
        break;
      case "event":
        if (!Name.test(it.name ?? "")) throw new BatchError("nome de evento invalido");
        batch.events.push({ sessionId, ts: at, name: it.name, props: props(it.props) });
        break;
      case "build":
        batch.builds.push({
          sessionId, ts: at,
          target: oneOf(Targets, it.target, "target"), result: oneOf(Results, it.result, "result"),
          durationMs: int(it.duration_ms), exitCode: int(it.exit_code),
          runtime: str(it.runtime, 48) ?? batch.app.runtime,
          errorText: str(it.error, 4000),
          errorSignature: it.error ? signatureOf("build", it.target, null, str(it.error, 4000)) : null,
        });
        break;
      case "crash": {
        const process = oneOf(Processes, it.process, "process");
        const kind = oneOf(Kinds, it.kind, "kind");
        const type = str(it.exception_type, 200);
        const message = str(it.message, 2000);
        const stack = str(it.stack, 64 * 1024);
        batch.crashes.push({
          sessionId, ts: at, process, kind, type, message, stack,
          logTail: str(it.log_tail, 64 * 1024),
          meta: props(it.meta),
          signature: signatureOf(process, kind, type, stack || message),
          title: titleOf(type, message, stack),
        });
        break;
      }
      default:
        break; // tipos novos do app sao ignorados por servidores antigos
    }
  }
  return batch;
}

// Quadros de pilha sem o que muda entre aparelhos e builds: enderecos, offsets,
// linhas, caminhos, ids de thread. Os 6 primeiros quadros uteis formam a assinatura.
export function normalizeFrames(stack) {
  if (!stack) return [];
  const frames = [];
  for (let line of stack.split(/\r?\n/)) {
    line = line.trim();
    if (!line) continue;
    if (!/^(at |#\d+|\d+:|frame|pc |\s*[\w.$<>]+\()/i.test(line) && !/\.so\b|\(\S+\.(java|cs|kt):/.test(line)) continue;
    const f = line
      .replace(/^#\d+\s+/, "")
      .replace(/\bpc\s+[0-9a-f]+/gi, "")
      .replace(/0x[0-9a-f]+/gi, "")
      .replace(/\+\s*\d+/g, "")
      .replace(/\[[^\]]*\]/g, "")
      .replace(/ in \S+:(line )?\d+/g, "")
      .replace(/:\d+\)/g, ")")
      .replace(/\S*\/([^/\s]+\.(so|apk|oat|odex|dex))\b/g, "$1")
      .replace(/\s+/g, " ")
      .trim();
    if (f) frames.push(f);
    if (frames.length === 6) break;
  }
  return frames;
}

export function signatureOf(process, kind, type, stack) {
  const frames = normalizeFrames(stack);
  const basis = [process, kind, type || "", ...(frames.length ? frames : [(stack || "").slice(0, 160).replace(/\d+/g, "#")])].join("\n");
  return createHash("sha1").update(basis).digest("hex").slice(0, 20);
}

export function titleOf(type, message, stack) {
  const first = normalizeFrames(stack)[0];
  const head = type || (message ? message.split("\n")[0] : "crash");
  return (first ? `${head} @ ${first}` : head).slice(0, 200);
}
