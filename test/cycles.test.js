import assert from "node:assert/strict";
import test from "node:test";
import { buildUsageCycles, normalizeBillingDay, ordinal } from "../public/cycles.js";

function day(date, totalTokens) {
  return {
    date,
    input_tokens: totalTokens - 10,
    cached_input_tokens: 20,
    output_tokens: 10,
    reasoning_output_tokens: 2,
    total_tokens: totalTokens,
    requests: 1,
    models: {
      "gpt-5.4": {
        input_tokens: totalTokens - 10,
        cached_input_tokens: 20,
        output_tokens: 10,
        reasoning_output_tokens: 2,
        total_tokens: totalTokens,
      },
    },
  };
}

test("groups usage into subscription cycles on the configured renewal day", () => {
  const model = buildUsageCycles(
    [
      day("2026-06-22", 100),
      day("2026-06-23", 200),
      day("2026-07-22", 300),
      day("2026-07-23", 400),
    ],
    23,
    new Date(2026, 6, 24, 12),
  );

  assert.equal(model.mode, "subscription");
  assert.equal(model.currentCycleKey, "2026-07-23");
  assert.equal(model.cycles[0].startDate, "2026-07-23");
  assert.equal(model.cycles[0].endDate, "2026-08-22");
  assert.equal(model.cycles[0].totals.total_tokens, 400);
  assert.equal(model.cycles[1].startDate, "2026-06-23");
  assert.equal(model.cycles[1].endDate, "2026-07-22");
  assert.equal(model.cycles[1].totals.total_tokens, 500);
  assert.equal(model.cycles[1].totals.models["gpt-5.4"].total_tokens, 500);
  assert.equal(model.cycles[2].totals.total_tokens, 100);
});

test("uses independent calendar months while the renewal day is unset", () => {
  const model = buildUsageCycles(
    [day("2026-06-30", 100), day("2026-07-01", 200)],
    null,
    new Date(2026, 6, 14, 12),
  );

  assert.equal(model.mode, "calendar");
  assert.equal(model.currentCycleKey, "2026-07-01");
  assert.equal(model.cycles[0].startDate, "2026-07-01");
  assert.equal(model.cycles[0].endDate, "2026-07-31");
  assert.equal(model.cycles[0].totals.total_tokens, 200);
  assert.equal(model.cycles[1].startDate, "2026-06-01");
  assert.equal(model.cycles[1].totals.total_tokens, 100);
});

test("clamps renewal days to the end of shorter months", () => {
  const model = buildUsageCycles(
    [day("2027-02-27", 100), day("2027-02-28", 200), day("2027-03-30", 300)],
    31,
    new Date(2027, 2, 30, 12),
  );

  assert.equal(model.currentCycleKey, "2027-02-28");
  assert.equal(model.cycles[0].endDate, "2027-03-30");
  assert.equal(model.cycles[0].totals.total_tokens, 500);
  assert.equal(model.cycles[1].endDate, "2027-02-27");
  assert.equal(model.cycles[1].totals.total_tokens, 100);
});

test("validates renewal days and formats ordinals", () => {
  assert.equal(normalizeBillingDay("23"), 23);
  assert.equal(normalizeBillingDay(""), null);
  assert.equal(normalizeBillingDay(32), null);
  assert.equal(ordinal(1), "1st");
  assert.equal(ordinal(12), "12th");
  assert.equal(ordinal(23), "23rd");
});
