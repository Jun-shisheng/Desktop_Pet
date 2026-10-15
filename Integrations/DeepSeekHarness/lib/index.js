// DeepSeek Fish Pet integration for DeepSeek Harness.
// Adapted from CrossPet's integrations/deepseek/dsh-plugin-crosspet/lib/index.js.
// Copyright (c) 2026 lokicorvus and CrossPet contributors. MIT license:
// see ../LICENSE-CrossPet.txt.
// Only pose/event/tool/timestamp and aggregate account balance are written locally.

import { mkdir, rename, unlink, writeFile } from "node:fs/promises";
import { homedir } from "node:os";
import { join } from "node:path";
import { lowThresholds, summarize } from "./balance.js";

export const name = "deepseek-fish-pet";

const STATE_FILE = "deepseek-state.json";
const QUOTA_FILE = "deepseek-quota.json";
const BIG_JOB_TOOLS = 8;
const BALANCE_EVERY_MS = 60_000;

function poseForTool(rawName) {
  const tool = String(rawName ?? "").toLowerCase();
  if (tool === "ask_user_question" || tool === "ask_user") return "asking";
  if (/image_gen|imagegen|generate_image|draw|paint/.test(tool)) return "drawing";
  if (/read|grep|glob|list|view|cat/.test(tool)) return "reading";
  if (/write|edit|patch|replace|create/.test(tool)) return "writing";
  if (/web|fetch|browse|search|http/.test(tool)) return "searching";
  if (/agent|task|subagent|delegate/.test(tool)) return "delegating";
  return "running";
}

function toolName(rawName) {
  // Tool arguments and responses may contain conversations or keys; never persist them.
  return String(rawName ?? "").slice(0, 128).replace(/[^A-Za-z0-9_.:/-]/g, "");
}

function failed(result) {
  return !!result && typeof result === "object" &&
    (result.isError === true || result.is_error === true || result.ok === false);
}

export function apply(ctx, config = {}) {
  const localAppData = process.env.LOCALAPPDATA || join(homedir(), "AppData", "Local");
  const directory = join(localAppData, "DeepSeekFishPet", "state");
  const pending = new Map();
  let sequence = 0;
  let tools = 0;
  let account = null;
  let lastPollAt = 0;
  let balanceInFlight = null;
  const balanceGapMs = Number.isFinite(config.balanceGapMs) && config.balanceGapMs >= 0
    ? config.balanceGapMs : 30_000;
  const lowBalance = lowThresholds(config.lowBalance);

  function writeJson(file, value) {
    // Serialize writes per file: a slow earlier event must not replace a newer pose.
    const previous = pending.get(file) || Promise.resolve();
    const current = previous.catch(() => {}).then(async () => {
      await mkdir(directory, { recursive: true });
      const temporary = join(directory, `.${file}.${process.pid}.${++sequence}`);
      try {
        await writeFile(temporary, JSON.stringify(value), "utf8");
        await rename(temporary, join(directory, file));
      } catch (error) {
        await unlink(temporary).catch(() => {});
        throw error;
      }
    });
    pending.set(file, current);
    current.then(
      () => { if (pending.get(file) === current) pending.delete(file); },
      () => { if (pending.get(file) === current) pending.delete(file); },
    );
    return current;
  }

  function state(pose, event, tool = "") {
    writeJson(STATE_FILE, { pose, event, tool: toolName(tool), ts: Date.now() / 1000 })
      .catch(() => ctx.logger?.warn?.("deepseek-fish-pet: state write failed"));
  }

  function quota(value) {
    return writeJson(QUOTA_FILE, { ...value, ts: Date.now() / 1000 }).catch(() => {
      ctx.logger?.warn?.("deepseek-fish-pet: balance write failed");
    });
  }

  function showBalance(entries, available = true) {
    const value = summarize(entries, { available, low: lowBalance });
    return value ? quota(value).then(() => "ok") : "no-balance";
  }

  async function balanceByKey() {
    // The credential stays inside Harness and this function's memory. It is never logged or written.
    const credentialService = ctx.get?.("credentials");
    const hit = await credentialService?.resolve?.("DEEPSEEK_API_KEY");
    if (!hit?.value) return "no-key";
    const response = await fetch("https://api.deepseek.com/user/balance", {
      headers: { Authorization: `Bearer ${hit.value}`, Accept: "application/json" },
      signal: AbortSignal.timeout(10_000),
    });
    if (!response.ok) return "http-error";
    const body = await response.json();
    const infos = Array.isArray(body?.balance_infos) ? body.balance_infos : [];
    return showBalance(infos.map((item) => ({
      currency: item?.currency,
      amount: item?.total_balance,
    })), body?.is_available !== false);
  }

  async function balanceByAccount() {
    if (!account?.getBalance) return "no-account";
    const result = await account.getBalance({
      version: "deepseek-fish-pet",
      locale: "zh-CN",
      timezoneOffsetSeconds: -new Date().getTimezoneOffset() * 60,
    });
    if (result === null) return "not-signed-in";
    if (result?.status !== "ready") return "failed";
    const wallets = [...(result.value || []), ...(result.bonusWallets || [])];
    return showBalance(wallets.map((wallet) => ({
      currency: wallet?.currency,
      amount: wallet?.balance,
    })));
  }

  function pollBalance() {
    if (config.balanceEnabled === false) return Promise.resolve();
    if (balanceInFlight) return balanceInFlight;
    lastPollAt = Date.now();
    balanceInFlight = (async () => {
      try {
        const byKey = await balanceByKey().catch(() => "failed");
        const byAccount = byKey === "ok" ? "not-tried" :
          await balanceByAccount().catch(() => "failed");
        if (byKey === "ok" || byAccount === "ok") return;
        if (byKey === "no-key" && ["no-account", "not-signed-in"].includes(byAccount)) {
          await quota({ text: "余额：请在 Harness 登录账号或填 API Key", low: false });
        } else {
          // Do not display an old numeric balance as if it were current.
          await quota({ text: "余额暂不可用", low: false, stale: true });
        }
      } catch {
        await quota({ text: "余额暂不可用", low: false, stale: true });
      }
    })().finally(() => { balanceInFlight = null; });
    return balanceInFlight;
  }

  function pollBalanceSoon() {
    if (Date.now() - lastPollAt >= balanceGapMs) void pollBalance();
  }

  state("idle", "Ready");
  ctx.on("agent/created", () => state("idle", "SessionStart"));
  ctx.on("agent/pre-step", async ({ messages }, next) => {
    if (messages?.length) {
      tools = 0;
      state("listening", "UserPromptSubmit");
      pollBalanceSoon();
    }
    return next();
  });
  ctx.on("tools/pre-execute", async (exec, next) => {
    tools += 1;
    state(poseForTool(exec?.name), "PreToolUse", exec?.name);
    return next();
  });
  ctx.on("tools/post-execute", async (exec, result, next) => {
    state(failed(result) ? "oops" : "thinking", "PostToolUse", exec?.name);
    return next();
  });
  ctx.on("agent/turn-stopping", () => {
    state(tools >= BIG_JOB_TOOLS ? "proud" : "happy", "Stop");
    tools = 0;
    pollBalanceSoon();
  });
  ctx.on("approval/request", async (_request, next) => {
    let waiting = false;
    const timer = setTimeout(() => {
      waiting = true;
      state("asking", "PermissionRequest");
    }, 400);
    try {
      return await next();
    } finally {
      clearTimeout(timer);
      if (waiting) state("thinking", "PermissionResolved");
    }
  });
  ctx.on("subagent/start", () => state("delegating", "SubagentStart"));
  ctx.on("subagent/end", () => state("thinking", "SubagentStop"));
  ctx.on("session/event", (_session, event) => {
    const kind = event?.data?.reason?.kind;
    if (event?.type === "compaction/start") state("compact", "PreCompact");
    else if (event?.type === "compaction/end") state("thinking", "PostCompact");
    else if (event?.type === "turn/end" && kind === "aborted") {
      tools = 0;
      state("surprised", "Stop");
    } else if (event?.type === "turn/end" && kind === "error") {
      tools = 0;
      state("oops", "Stop");
    }
  });

  if (config.balanceEnabled !== false) {
    // Optional services must not block the state listener when they are absent.
    ctx.inject?.(["deepseekAccount"], (sub) => {
      account = sub.deepseekAccount;
      sub.effect(() => {
        void pollBalance();
        return () => { account = null; };
      }, "deepseek-fish-pet: account balance");
    });
    ctx.effect(() => {
      void pollBalance();
      const timer = setInterval(() => { void pollBalance(); }, BALANCE_EVERY_MS);
      return () => clearInterval(timer);
    }, "deepseek-fish-pet: balance timer");
  }
}
