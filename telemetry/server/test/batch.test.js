import { test } from "node:test";
import assert from "node:assert/strict";
import { parseBatch, signatureOf, normalizeFrames, BatchError, Schema } from "../lib/batch.js";

const id = (n) => `00000000-0000-4000-8000-${String(n).padStart(12, "0")}`;
const base = (items) => ({ schema: Schema, batch_id: id(1), install_id: id(2), app: { version: "0.2.2", code: 4 }, device: { model: "SM-S918B", api: 34 }, items });

test("aceita um lote com todos os tipos", () => {
  const now = Date.parse("2026-11-08T12:00:00Z");
  const b = parseBatch(base([
    { type: "session_start", session_id: id(3), ts: now - 1000, boot_ms: 8200 },
    { type: "session_heartbeat", session_id: id(3), ts: now },
    { type: "event", session_id: id(3), ts: now, name: "apk.export", props: { ok: true, ms: 1200, "bad key": 1 } },
    { type: "build", session_id: id(3), ts: now, target: "run_gx", result: "gml_error", duration_ms: 3400, exit_code: 1, error: "gml_Object_obj_player_Step_0(12): unknown function" },
    { type: "crash", session_id: id(3), ts: now, process: "build", kind: "managed", exception_type: "System.NullReferenceException", stack: "  at Igor.Foo.Bar () [0x0001c] in /src/Igor.cs:12\n  at Igor.Main ()" },
    { type: "novo_tipo", ts: now },
  ]), now);
  assert.equal(b.sessions.length, 1);
  assert.equal(b.events[0].props["bad key"], undefined);
  assert.equal(b.builds[0].result, "gml_error");
  assert.ok(b.builds[0].errorSignature);
  assert.equal(b.crashes[0].signature.length, 20);
  assert.match(b.crashes[0].title, /NullReferenceException @ at Igor.Foo.Bar/);
});

test("recusa schema, ids e enums invalidos", () => {
  assert.throws(() => parseBatch({ ...base([]), schema: "x" }), BatchError);
  assert.throws(() => parseBatch({ ...base([]), install_id: "abc" }), BatchError);
  assert.throws(() => parseBatch(base([{ type: "build", target: "ps5", result: "ok" }])), BatchError);
  assert.throws(() => parseBatch(base([{ type: "crash", process: "ide", kind: "x" }])), BatchError);
});

test("relogio errado vira o horario do servidor", () => {
  const now = Date.parse("2026-11-08T12:00:00Z");
  const b = parseBatch(base([{ type: "event", name: "x", ts: "1999-01-01T00:00:00Z" }]), now);
  assert.equal(b.events[0].ts, new Date(now).toISOString());
});

test("assinatura ignora enderecos, offsets e linhas", () => {
  const a = "#00 pc 0000000000012345  /data/app/~~abc==/io.gmide.android-1/lib/arm64/libgmhost.so (crash_handler+44)\n#01 pc 00000000000abcd  /system/lib64/libc.so (abort+164)";
  const b = "#00 pc 0000000000099999  /data/app/~~xyz==/io.gmide.android-2/lib/arm64/libgmhost.so (crash_handler+48)\n#01 pc 0000000000000ff  /system/lib64/libc.so (abort+170)";
  assert.equal(signatureOf("ide", "native", "SIGSEGV", a), signatureOf("ide", "native", "SIGSEGV", b));
  assert.notEqual(signatureOf("ide", "native", "SIGSEGV", a), signatureOf("game", "native", "SIGSEGV", a));
  const m1 = "  at A.B () [0x00010] in /x/A.cs:10\n  at C.D ()";
  const m2 = "  at A.B () [0x00020] in /y/A.cs:99\n  at C.D ()";
  assert.equal(signatureOf("ide", "managed", "E", m1), signatureOf("ide", "managed", "E", m2));
  assert.equal(normalizeFrames(m1)[0], "at A.B ()");
});
