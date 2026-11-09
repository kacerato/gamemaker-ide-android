// Rota /api/v1/ingest de ponta a ponta, com o banco trocado por um dublê (nada vai para o Neon).
// Reproduz o lote que o app monta: arquivo b-*.json com BOM UTF-8, gzip, content-type JSON.
import { test, mock } from "node:test";
import assert from "node:assert/strict";
import { Readable } from "node:stream";
import { gzipSync } from "node:zlib";
import { randomUUID } from "node:crypto";

const queries = [];
const fakeSql = (strings, ...values) => {
  const text = strings.join("?");
  queries.push(text);
  if (/with recent as/.test(text)) return Promise.resolve([{ recent: 0, inserted: 1 }]);
  return Promise.resolve([]);
};
fakeSql.transaction = async (list) => list;
mock.module("../lib/db.js", { namedExports: { db: () => fakeSql } });
const { default: ingest } = await import("../api/v1/ingest.js");

// Igual a TelemetryUploader.WriteBatch: cabecalho + itens JSONL, valores de smoke-test.
function smokeBatch() {
  const session = randomUUID(), now = Date.now();
  return JSON.stringify({
    schema: "gmide.telemetry/1", batch_id: randomUUID(), install_id: randomUUID(),
    app: { runtime: "2026.0.0.23", version: "0.0.0-smoke", code: 0 },
    device: { model: "smoke-test", manufacturer: "smoke-test", api: 35, abi: "arm64-v8a", locale: "pt-BR", gpu: "smoke", ram_mb: 1 },
    items: [
      { type: "session_start", session_id: session, ts: now, boot_ms: 1 },
      { type: "event", session_id: session, ts: now, name: "smoke.test", props: { texto: "ação ✓ \"aspas\" \\ \u0001" } },
      { type: "crash", session_id: session, ts: now, process: "ide", kind: "managed", exception_type: "System.Exception",
        message: "falha: çãé — 日本語 😀\nlinha 2\t\"x\"", stack: "  at A.B () [0x0001c] in /src/A.cs:12" },
    ],
  });
}

async function post(body, headers) {
  const req = Readable.from([body]);
  req.method = "POST";
  req.headers = headers;
  const res = { statusCode: 0, headers: {}, body: "", setHeader(k, v) { this.headers[k] = v; }, end(b) { this.body = b; } };
  await ingest(req, res);
  return { status: res.statusCode, body: JSON.parse(res.body) };
}

const Bom = Buffer.from([0xef, 0xbb, 0xbf]);
const appHeaders = { "content-type": "application/json", "content-encoding": "gzip" };

test("aceita o lote do app gravado com BOM UTF-8 (File.WriteAllText com Encoding.UTF8)", async () => {
  const raw = Buffer.concat([Bom, Buffer.from(smokeBatch(), "utf8")]);
  const r = await post(gzipSync(raw), appHeaders);
  assert.equal(r.status, 200, JSON.stringify(r.body));
  assert.equal(r.body.items, 3);
});

test("aceita o lote sem BOM", async () => {
  const r = await post(gzipSync(Buffer.from(smokeBatch(), "utf8")), appHeaders);
  assert.equal(r.status, 200, JSON.stringify(r.body));
});

test("continua recusando JSON quebrado e gzip invalido", async () => {
  assert.deepEqual(await post(gzipSync(Buffer.from("{\"schema\":", "utf8")), appHeaders), { status: 400, body: { error: "json" } });
  assert.deepEqual(await post(Buffer.from("nao e gzip"), appHeaders), { status: 400, body: { error: "gzip" } });
});
