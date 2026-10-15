// Adapted from CrossPet's integrations/deepseek/dsh-plugin-crosspet/lib/balance.js.
// Copyright (c) 2026 lokicorvus and CrossPet contributors. MIT license:
// see ../LICENSE-CrossPet.txt. Only aggregate balance numbers are returned.

const SYMBOL = { CNY: "¥", USD: "$" };
const ORDER = ["CNY", "USD"];

export const DEFAULT_LOW = { CNY: 5, USD: 0.7 };

export function lowThresholds(config) {
  if (typeof config === "number" && Number.isFinite(config) && config >= 0) {
    return { ...DEFAULT_LOW, CNY: config };
  }
  if (config && typeof config === "object" && !Array.isArray(config)) {
    const low = { ...DEFAULT_LOW };
    for (const currency of ORDER) {
      const value = Number(config[currency]);
      if (Number.isFinite(value) && value >= 0) low[currency] = value;
    }
    return low;
  }
  return { ...DEFAULT_LOW };
}

export function summarize(entries, { available = true, low = DEFAULT_LOW } = {}) {
  const totals = Object.create(null);
  for (const entry of entries || []) {
    const currency = String(entry?.currency || "").toUpperCase();
    const amount = Number(entry?.amount);
    if (!currency || !Number.isFinite(amount)) continue;
    totals[currency] = (totals[currency] || 0) + amount;
  }
  const currencies = Object.keys(totals).sort((a, b) => rank(a) - rank(b) || a.localeCompare(b));
  if (!currencies.length) return null;

  const balances = {};
  for (const currency of currencies) balances[currency] = round2(totals[currency]);
  const positive = currencies.filter((currency) => balances[currency] > 0);
  const shown = positive.length ? positive : currencies.slice(0, 1);
  const text = "余额 " + shown.map((currency) => format(currency, balances[currency])).join("｜");
  const primary = currencies[0];
  return {
    text,
    balance: balances[primary],
    balances,
    low: !available || currencies.every((currency) =>
      balances[currency] < (low[currency] ?? low.CNY ?? DEFAULT_LOW.CNY)),
  };
}

function rank(currency) {
  const index = ORDER.indexOf(currency);
  return index < 0 ? ORDER.length : index;
}

function round2(amount) { return Math.round(amount * 100) / 100; }
function format(currency, amount) { return (SYMBOL[currency] ?? `${currency} `) + amount.toFixed(2); }
