import { timingSafeEqual } from "node:crypto";
import { gunzipSync } from "node:zlib";

export function reply(res, status, body) {
  res.statusCode = status;
  res.setHeader("content-type", "application/json; charset=utf-8");
  res.setHeader("cache-control", "no-store");
  res.end(JSON.stringify(body));
}

// Le o corpo cru (o app manda gzip), com teto antes e depois de descomprimir.
export async function readJson(req, maxBytes, maxInflated = maxBytes * 8) {
  const chunks = [];
  let size = 0;
  for await (const chunk of req) {
    size += chunk.length;
    if (size > maxBytes) throw new HttpError(413, "tamanho");
    chunks.push(chunk);
  }
  let buf = Buffer.concat(chunks);
  if ((req.headers["content-encoding"] || "").toLowerCase() === "gzip") {
    try { buf = gunzipSync(buf, { maxOutputLength: maxInflated }); }
    catch { throw new HttpError(400, "gzip"); }
  }
  try { return JSON.parse(buf.toString("utf8")); }
  catch { throw new HttpError(400, "json"); }
}

export class HttpError extends Error {
  constructor(status, message) { super(message); this.status = status; }
}

function safeEqual(a, b) {
  const x = Buffer.from(a), y = Buffer.from(b);
  return x.length === y.length && timingSafeEqual(x, y);
}

// Painel e cron: "Authorization: Bearer <segredo>".
export function requireBearer(req, secret) {
  const header = req.headers.authorization || "";
  if (!secret || !header.startsWith("Bearer ") || !safeEqual(header.slice(7), secret))
    throw new HttpError(401, "nao autorizado");
}

export function handle(fn) {
  return async (req, res) => {
    try { await fn(req, res); }
    catch (e) {
      if (e instanceof HttpError) return reply(res, e.status, { error: e.message });
      console.error("[gmide-telemetry]", e);
      return reply(res, 500, { error: "interno" });
    }
  };
}
