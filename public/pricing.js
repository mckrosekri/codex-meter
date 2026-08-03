export const PRICING_UPDATED_AT = "2026-07-14";
export const PRICING_SOURCE_URL = "https://developers.openai.com/api/docs/models/compare";
export const DEFAULT_MODEL_ID = "gpt-5.5";

export const PRICING_MODELS = [
  { id: "gpt-5.6-sol", label: "GPT-5.6 Sol", input: 5, cachedInput: 0.5, output: 30 },
  { id: "gpt-5.6-terra", label: "GPT-5.6 Terra", input: 2.5, cachedInput: 0.25, output: 15 },
  { id: "gpt-5.6-luna", label: "GPT-5.6 Luna", input: 1, cachedInput: 0.1, output: 6 },
  { id: "gpt-5.5", label: "GPT-5.5", input: 5, cachedInput: 0.5, output: 30 },
  { id: "gpt-5.4", label: "GPT-5.4", input: 2.5, cachedInput: 0.25, output: 15 },
  { id: "gpt-5.4-mini", label: "GPT-5.4 mini", input: 0.75, cachedInput: 0.075, output: 4.5 },
  { id: "gpt-5.3-codex", label: "GPT-5.3-Codex", input: 1.75, cachedInput: 0.175, output: 14 },
  { id: "gpt-5.2", label: "GPT-5.2", input: 1.75, cachedInput: 0.175, output: 14 },
  { id: "gpt-5", label: "GPT-5", input: 1.25, cachedInput: 0.125, output: 10 },
];

const prices = new Map(PRICING_MODELS.map((model) => [model.id, model]));

function number(value) {
  const parsed = Number(value);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : 0;
}

function atRates(totals, rates) {
  const inputTokens = number(totals?.input_tokens);
  const cachedInputTokens = Math.min(inputTokens, number(totals?.cached_input_tokens));
  const uncachedInputTokens = Math.max(0, inputTokens - cachedInputTokens);
  const outputTokens = number(totals?.output_tokens);
  return {
    uncachedInput: (uncachedInputTokens / 1_000_000) * rates.input,
    cachedInput: (cachedInputTokens / 1_000_000) * rates.cachedInput,
    output: (outputTokens / 1_000_000) * rates.output,
  };
}

function addCost(target, value) {
  target.uncachedInput += value.uncachedInput;
  target.cachedInput += value.cachedInput;
  target.output += value.output;
}

function finish(cost) {
  return { ...cost, total: cost.uncachedInput + cost.cachedInput + cost.output };
}

export function normalizePricingBasis(value) {
  return value === "auto" || prices.has(value) ? value : "auto";
}

export function estimateApiCost(totals, basisValue = "auto") {
  const basis = normalizePricingBasis(basisValue);
  if (basis !== "auto") {
    const model = prices.get(basis);
    return {
      basis,
      label: model.label,
      cost: finish(atRates(totals, model)),
      recordedModels: Object.keys(totals?.models ?? {}).length,
      fallbackModels: [],
    };
  }

  const entries = Object.entries(totals?.models ?? {});
  if (!entries.length) {
    const fallback = prices.get(DEFAULT_MODEL_ID);
    return {
      basis: "auto",
      label: `${fallback.label} fallback`,
      cost: finish(atRates(totals, fallback)),
      recordedModels: 0,
      fallbackModels: ["unattributed"],
    };
  }

  const cost = { uncachedInput: 0, cachedInput: 0, output: 0 };
  const fallback = prices.get(DEFAULT_MODEL_ID);
  const fallbackModels = [];
  for (const [modelId, modelTotals] of entries) {
    const rates = prices.get(modelId) ?? fallback;
    if (!prices.has(modelId)) fallbackModels.push(modelId);
    addCost(cost, atRates(modelTotals, rates));
  }

  return {
    basis: "auto",
    label: "Recorded models",
    cost: finish(cost),
    recordedModels: entries.length,
    fallbackModels,
  };
}
