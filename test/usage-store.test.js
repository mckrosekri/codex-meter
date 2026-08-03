import assert from "node:assert/strict";
import { mkdir, mkdtemp, rm, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { UsageStore } from "../src/usage-store.js";

function line(timestamp, payload) {
  return JSON.stringify({ timestamp, type: payload.type === "session_meta" ? "session_meta" : "event_msg", payload });
}

function tokenEvent(timestamp, total, last, usedPercent = 20) {
  return line(timestamp, {
    type: "token_count",
    info: {
      total_token_usage: total,
      last_token_usage: last,
      model_context_window: 200_000,
    },
    rate_limits: {
      limit_id: "codex",
      primary: { used_percent: usedPercent, window_minutes: 300, resets_at: 1_800_000_000 },
      secondary: { used_percent: 55, window_minutes: 10080, resets_at: 1_800_100_000 },
      plan_type: "pro",
      credits: null,
    },
  });
}

function turnContext(timestamp, model) {
  return JSON.stringify({ timestamp, type: "turn_context", payload: { model } });
}

test("aggregates exact cumulative deltas and ignores repeated token events", async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), "codex-meter-test-"));
  const codexHome = path.join(root, ".codex");
  const sessionDirectory = path.join(codexHome, "sessions", "2026", "07", "14");
  await mkdir(sessionDirectory, { recursive: true });
  const sessionFile = path.join(sessionDirectory, "rollout-test.jsonl");

  const first = { input_tokens: 100, cached_input_tokens: 40, output_tokens: 20, reasoning_output_tokens: 5, total_tokens: 120 };
  const second = { input_tokens: 250, cached_input_tokens: 100, output_tokens: 50, reasoning_output_tokens: 15, total_tokens: 300 };
  await writeFile(
    sessionFile,
    [
      line("2026-07-14T08:00:00.000Z", { type: "session_meta", session_id: "session-1", cwd: "/work/private" }),
      JSON.stringify({ timestamp: "2026-07-14T08:00:01.000Z", type: "event_msg", payload: { type: "user_message", message: "secret prompt" } }),
      turnContext("2026-07-14T08:00:30.000Z", "gpt-5.4"),
      tokenEvent("2026-07-14T08:01:00.000Z", first, first, 20),
      tokenEvent("2026-07-14T08:01:01.000Z", first, first, 21),
      tokenEvent(
        "2026-07-14T08:02:00.000Z",
        second,
        { input_tokens: 150, cached_input_tokens: 60, output_tokens: 30, reasoning_output_tokens: 10, total_tokens: 180 },
        22,
      ),
      "",
    ].join("\n"),
    "utf8",
  );

  const store = new UsageStore({
    codexHome,
    cacheDir: path.join(root, "cache"),
    now: () => new Date("2026-07-14T12:00:00.000Z"),
  });
  await store.initialize({ waitForScan: true });
  const snapshot = store.getSnapshot();

  assert.equal(snapshot.activity.allTime.total_tokens, 300);
  assert.equal(snapshot.activity.allTime.input_tokens, 250);
  assert.equal(snapshot.activity.allTime.cached_input_tokens, 100);
  assert.equal(snapshot.activity.allTime.output_tokens, 50);
  assert.equal(snapshot.activity.allTime.requests, 2);
  assert.equal(snapshot.activity.daily.length, 1);
  assert.equal(snapshot.activity.daily[0].date, "2026-07-14");
  assert.equal(snapshot.activity.daily[0].total_tokens, 300);
  assert.equal(snapshot.activity.daily[0].models["gpt-5.4"].total_tokens, 300);
  assert.equal(snapshot.activity.allTime.models["gpt-5.4"].cached_input_tokens, 100);
  assert.equal(snapshot.limits.length, 2);
  assert.equal(snapshot.limits[0].usedPercent, 22);
  assert.equal(snapshot.limits[1].usedPercent, 55);
  assert.equal(snapshot.account.plan, "pro");
  assert.doesNotMatch(JSON.stringify(snapshot), /secret prompt|\/work\/private/);

  await rm(root, { recursive: true, force: true });
});

test("uses the cache and only adds appended cumulative usage", async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), "codex-meter-cache-test-"));
  const codexHome = path.join(root, ".codex");
  const sessionDirectory = path.join(codexHome, "sessions");
  await mkdir(sessionDirectory, { recursive: true });
  const sessionFile = path.join(sessionDirectory, "rollout-test.jsonl");
  const first = { input_tokens: 80, cached_input_tokens: 20, output_tokens: 20, reasoning_output_tokens: 4, total_tokens: 100 };
  await writeFile(
    sessionFile,
    `${line("2026-07-14T08:00:00.000Z", { type: "session_meta", session_id: "session-2" })}\n${turnContext("2026-07-14T08:00:30.000Z", "gpt-5.4")}\n${tokenEvent("2026-07-14T08:01:00.000Z", first, first)}\n`,
  );

  const options = {
    codexHome,
    cacheDir: path.join(root, "cache"),
    now: () => new Date("2026-07-14T12:00:00.000Z"),
  };
  const firstStore = new UsageStore(options);
  await firstStore.initialize({ waitForScan: true });
  assert.equal(firstStore.getSnapshot().activity.allTime.total_tokens, 100);

  const second = { input_tokens: 150, cached_input_tokens: 40, output_tokens: 50, reasoning_output_tokens: 10, total_tokens: 200 };
  await writeFile(
    sessionFile,
    `${line("2026-07-14T08:00:00.000Z", { type: "session_meta", session_id: "session-2" })}\n${turnContext("2026-07-14T08:00:30.000Z", "gpt-5.4")}\n${tokenEvent("2026-07-14T08:01:00.000Z", first, first)}\n${turnContext("2026-07-14T08:01:30.000Z", "gpt-5.6-sol")}\n${tokenEvent("2026-07-14T08:02:00.000Z", second, { input_tokens: 70, cached_input_tokens: 20, output_tokens: 30, reasoning_output_tokens: 6, total_tokens: 100 }, 35)}\n`,
  );

  const secondStore = new UsageStore(options);
  await secondStore.initialize({ waitForScan: true });
  const snapshot = secondStore.getSnapshot();
  assert.equal(snapshot.activity.allTime.total_tokens, 200);
  assert.equal(snapshot.activity.allTime.requests, 2);
  assert.equal(snapshot.activity.daily[0].total_tokens, 200);
  assert.equal(snapshot.activity.daily[0].models["gpt-5.4"].total_tokens, 100);
  assert.equal(snapshot.activity.daily[0].models["gpt-5.6-sol"].total_tokens, 100);
  assert.equal(snapshot.limits[0].usedPercent, 35);

  await rm(root, { recursive: true, force: true });
});
