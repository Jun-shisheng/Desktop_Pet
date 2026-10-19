import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { join } from "node:path";
import { apply, name } from "./lib/index.js";
import { summarize } from "./lib/balance.js";

const sample = summarize([
  { currency: "USD", amount: "2" },
  { currency: "CNY", amount: "1" },
  { currency: "USD", amount: "3" },
]);
assert.deepEqual(sample.balances, { CNY: 1, USD: 5 });
assert.equal(sample.text, "余额 ¥1.00｜$5.00");

const handlers = new Map();
const ctx = {
  on: (event, handler) => handlers.set(event, handler),
  effect: (handler) => handler(),
  inject: () => {},
  get: () => undefined,
  logger: { warn: () => {} },
};
// The default integration observes work events without querying account balance.
apply(ctx);
assert.equal(name, "deepseek-fish-pet");
const file = join(process.env.LOCALAPPDATA, "DeepSeekFishPet", "state", "deepseek-state.json");
const next = async () => "ok";
await handlers.get("agent/pre-step")({ messages: [{ content: "SECRET_MESSAGE" }] }, next);
await handlers.get("tools/pre-execute")({ name: "read_file", arguments: { password: "SECRET_KEY" } }, next);
await handlers.get("tools/post-execute")({ name: "read_file" }, { ok: true, content: "SECRET_RESULT" }, next);
handlers.get("agent/turn-stopping")();
await new Promise((resolve) => setTimeout(resolve, 200));
let raw = await readFile(file, "utf8");
let value = JSON.parse(raw);
assert.equal(value.pose, "happy");
assert.equal(value.event, "Stop");
assert.equal(value.tool, "");
assert.equal(typeof value.ts, "number");
assert.ok(!raw.includes("SECRET"));

await handlers.get("approval/request")({}, () => new Promise((resolve) => setTimeout(resolve, 450)));
await new Promise((resolve) => setTimeout(resolve, 100));
value = JSON.parse(await readFile(file, "utf8"));
assert.equal(value.pose, "thinking");
handlers.get("session/event")({}, { type: "turn/end", data: { reason: { kind: "aborted" } } });
await new Promise((resolve) => setTimeout(resolve, 100));
value = JSON.parse(await readFile(file, "utf8"));
assert.equal(value.pose, "surprised");
console.log("mock Harness state and balance summary: OK");
