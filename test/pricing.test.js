import assert from "node:assert/strict";
import test from "node:test";
import { estimateApiCost, normalizePricingBasis } from "../public/pricing.js";

test("prices uncached input, cached input, and output independently", () => {
  const estimate = estimateApiCost(
    { input_tokens: 2_000_000, cached_input_tokens: 1_000_000, output_tokens: 500_000 },
    "gpt-5.4",
  );

  assert.equal(estimate.cost.uncachedInput, 2.5);
  assert.equal(estimate.cost.cachedInput, 0.25);
  assert.equal(estimate.cost.output, 7.5);
  assert.equal(estimate.cost.total, 10.25);
});

test("automatically uses the recorded model mix", () => {
  const estimate = estimateApiCost({
    models: {
      "gpt-5.4": { input_tokens: 1_000_000, cached_input_tokens: 0, output_tokens: 0 },
      "gpt-5.6-sol": { input_tokens: 0, cached_input_tokens: 0, output_tokens: 1_000_000 },
    },
  });

  assert.equal(estimate.cost.total, 32.5);
  assert.equal(estimate.recordedModels, 2);
  assert.deepEqual(estimate.fallbackModels, []);
});

test("uses a disclosed fallback for future unpriced models", () => {
  const estimate = estimateApiCost({
    models: {
      "future-model": { input_tokens: 1_000_000, cached_input_tokens: 0, output_tokens: 0 },
    },
  });

  assert.equal(estimate.cost.total, 5);
  assert.deepEqual(estimate.fallbackModels, ["future-model"]);
  assert.equal(normalizePricingBasis("not-a-model"), "auto");
});
