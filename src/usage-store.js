import { createReadStream } from "node:fs";
import {
  mkdir,
  open,
  readFile,
  readdir,
  stat,
  writeFile,
} from "node:fs/promises";
import { createInterface } from "node:readline";
import os from "node:os";
import path from "node:path";

const CACHE_VERSION = 2;
const TOKEN_KEYS = [
  "input_tokens",
  "cached_input_tokens",
  "output_tokens",
  "reasoning_output_tokens",
  "total_tokens",
];
const RECENT_FILE_COUNT = 16;
const TAIL_BYTES = 1024 * 1024;

function emptyTokens() {
  return {
    input_tokens: 0,
    cached_input_tokens: 0,
    output_tokens: 0,
    reasoning_output_tokens: 0,
    total_tokens: 0,
  };
}

function emptyDay() {
  return { ...emptyTokens(), requests: 0, models: {} };
}

function numberOrZero(value) {
  return Number.isFinite(Number(value)) ? Number(value) : 0;
}

function normalizeTokens(value) {
  const normalized = emptyTokens();
  for (const key of TOKEN_KEYS) normalized[key] = numberOrZero(value?.[key]);
  return normalized;
}

function addTokens(target, source) {
  for (const key of TOKEN_KEYS) target[key] += numberOrZero(source?.[key]);
  return target;
}

function modelName(value) {
  const normalized = typeof value === "string" ? value.trim().toLowerCase() : "";
  return normalized || "unknown";
}

function addModelTokens(target, model, tokens) {
  const key = modelName(model);
  target[key] ??= emptyTokens();
  addTokens(target[key], tokens);
}

function mergeModelTokens(target, source) {
  for (const [model, tokens] of Object.entries(source ?? {})) {
    addModelTokens(target, model, tokens);
  }
}

function subtractTokens(current, previous) {
  const delta = emptyTokens();
  for (const key of TOKEN_KEYS) {
    delta[key] = numberOrZero(current?.[key]) - numberOrZero(previous?.[key]);
  }
  return delta;
}

function hasNegativeToken(delta) {
  return TOKEN_KEYS.some((key) => delta[key] < 0);
}

function localDateKey(value) {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return null;
  const year = date.getFullYear();
  const month = String(date.getMonth() + 1).padStart(2, "0");
  const day = String(date.getDate()).padStart(2, "0");
  return `${year}-${month}-${day}`;
}

function newRecord(filePath, fileStat) {
  return {
    path: filePath,
    size: fileStat.size,
    mtimeMs: fileStat.mtimeMs,
    offset: 0,
    sessionId: null,
    startedAt: null,
    lastEventAt: null,
    totals: emptyTokens(),
    byModel: {},
    requests: 0,
    byDay: {},
    currentModel: null,
    lastCumulative: null,
    latestRateLimits: null,
    latestRateTimestamp: null,
  };
}

function safeTimestamp(value) {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? null : date.toISOString();
}

async function listJsonlFiles(root) {
  const output = [];

  async function visit(directory) {
    let entries;
    try {
      entries = await readdir(directory, { withFileTypes: true });
    } catch (error) {
      if (error.code === "ENOENT") return;
      throw error;
    }

    for (const entry of entries) {
      const fullPath = path.join(directory, entry.name);
      if (entry.isDirectory()) await visit(fullPath);
      else if (entry.isFile() && entry.name.endsWith(".jsonl")) output.push(fullPath);
    }
  }

  await visit(root);
  return output;
}

function processTelemetryLine(line, record) {
  if (
    !line.includes('"type":"token_count"') &&
    !line.includes('"type":"session_meta"') &&
    !line.includes('"type":"turn_context"')
  ) {
    return;
  }

  let event;
  try {
    event = JSON.parse(line);
  } catch {
    return;
  }

  if (event.type === "session_meta") {
    const payload = event.payload ?? {};
    record.sessionId ??= payload.session_id ?? payload.id ?? null;
    record.startedAt ??= safeTimestamp(payload.timestamp ?? event.timestamp);
    return;
  }

  if (event.type === "turn_context") {
    record.currentModel = modelName(event.payload?.model);
    return;
  }

  const payload = event.payload;
  if (payload?.type !== "token_count") return;

  const timestamp = safeTimestamp(event.timestamp);
  if (timestamp) record.lastEventAt = timestamp;

  if (payload.rate_limits && timestamp) {
    record.latestRateLimits = payload.rate_limits;
    record.latestRateTimestamp = timestamp;
  }

  const cumulativeValue = payload.info?.total_token_usage;
  const lastValue = payload.info?.last_token_usage;
  if (!cumulativeValue && !lastValue) return;

  const cumulative = cumulativeValue ? normalizeTokens(cumulativeValue) : null;
  const last = lastValue ? normalizeTokens(lastValue) : null;
  let delta;

  if (cumulative && record.lastCumulative) {
    delta = subtractTokens(cumulative, record.lastCumulative);
    if (hasNegativeToken(delta)) delta = last ?? cumulative;
  } else {
    delta = cumulative ?? last;
  }

  if (cumulative) record.lastCumulative = cumulative;
  if (!delta || delta.total_tokens <= 0) return;

  addTokens(record.totals, delta);
  addModelTokens(record.byModel, record.currentModel, delta);
  record.requests += 1;

  const dayKey = timestamp ? localDateKey(timestamp) : null;
  if (dayKey) {
    record.byDay[dayKey] ??= emptyDay();
    addTokens(record.byDay[dayKey], delta);
    addModelTokens(record.byDay[dayKey].models, record.currentModel, delta);
    record.byDay[dayKey].requests += 1;
  }
}

async function parseFileAppend(filePath, fileStat, cachedRecord, force) {
  const canAppend =
    !force &&
    cachedRecord &&
    cachedRecord.offset > 0 &&
    cachedRecord.offset <= fileStat.size;
  const record = canAppend
    ? structuredClone(cachedRecord)
    : newRecord(filePath, fileStat);
  const start = canAppend ? cachedRecord.offset : 0;

  if (start < fileStat.size) {
    const stream = createReadStream(filePath, {
      encoding: "utf8",
      start,
      end: fileStat.size - 1,
    });
    const lines = createInterface({ input: stream, crlfDelay: Infinity });
    for await (const line of lines) processTelemetryLine(line, record);
  }

  record.path = filePath;
  record.size = fileStat.size;
  record.offset = fileStat.size;
  record.mtimeMs = fileStat.mtimeMs;
  record.sessionId ??= path.basename(filePath, ".jsonl");
  return record;
}

async function readTail(filePath, bytes = TAIL_BYTES) {
  const fileStat = await stat(filePath);
  const length = Math.min(fileStat.size, bytes);
  if (length <= 0) return "";
  const handle = await open(filePath, "r");
  try {
    const buffer = Buffer.alloc(length);
    await handle.read(buffer, 0, length, fileStat.size - length);
    return buffer.toString("utf8");
  } finally {
    await handle.close();
  }
}

function latestRateFromTail(content) {
  const lines = content.split(/\r?\n/);
  for (let index = lines.length - 1; index >= 0; index -= 1) {
    const line = lines[index];
    if (!line.includes('"type":"token_count"') || !line.includes('"rate_limits"')) continue;
    try {
      const event = JSON.parse(line);
      if (event.payload?.type === "token_count" && event.payload.rate_limits) {
        return {
          rateLimits: event.payload.rate_limits,
          timestamp: safeTimestamp(event.timestamp),
        };
      }
    } catch {
      // A tail can begin in the middle of a JSON line. Continue backwards.
    }
  }
  return null;
}

function windowName(minutes) {
  if (minutes === 300) return "5-hour limit";
  if (minutes === 1440) return "Daily limit";
  if (minutes === 10080) return "Weekly limit";
  if (minutes && minutes % 1440 === 0) return `${minutes / 1440}-day limit`;
  if (minutes && minutes % 60 === 0) return `${minutes / 60}-hour limit`;
  return minutes ? `${minutes}-minute limit` : "Usage limit";
}

function normalizedReset(value) {
  const numeric = Number(value);
  if (!Number.isFinite(numeric)) return null;
  const milliseconds = numeric < 1e12 ? numeric * 1000 : numeric;
  return safeTimestamp(milliseconds);
}

function flattenLimits(rateEntries, now) {
  const limits = [];
  const seen = new Set();

  for (const entry of rateEntries) {
    const rate = entry.rateLimits ?? {};
    for (const slot of ["primary", "secondary"]) {
      const window = rate[slot];
      if (!window || !Number.isFinite(Number(window.used_percent))) continue;
      const windowMinutes = numberOrZero(window.window_minutes);
      const key = `${rate.limit_id ?? "codex"}:${windowMinutes}:${slot}`;
      const dedupeKey = `${rate.limit_id ?? "codex"}:${windowMinutes}`;
      if (seen.has(dedupeKey)) continue;
      seen.add(dedupeKey);

      const usedPercent = Number(window.used_percent);
      const resetsAt = normalizedReset(window.resets_at);
      if (resetsAt && new Date(resetsAt).getTime() <= now.getTime()) continue;
      limits.push({
        id: key,
        limitId: rate.limit_id ?? "codex",
        name: rate.limit_name || windowName(windowMinutes),
        slot,
        usedPercent,
        remainingPercent: Math.max(0, 100 - usedPercent),
        windowMinutes,
        resetsAt,
        observedAt: entry.timestamp,
        state: usedPercent >= 90 ? "critical" : usedPercent >= 70 ? "warning" : "normal",
      });
    }
  }

  return limits.sort((a, b) => a.windowMinutes - b.windowMinutes);
}

function addDay(target, source) {
  addTokens(target, source);
  mergeModelTokens(target.models, source?.models);
  target.requests += numberOrZero(source?.requests);
}

function allDailySeries(daysMap) {
  return Object.entries(daysMap)
    .sort(([left], [right]) => left.localeCompare(right))
    .map(([date, totals]) => ({ date, ...totals }));
}

function newestBySession(records) {
  const sessions = new Map();
  for (const record of records) {
    const key = record.sessionId || record.path;
    const existing = sessions.get(key);
    if (!existing) {
      sessions.set(key, record);
      continue;
    }
    const existingTime = new Date(existing.lastEventAt ?? existing.startedAt ?? 0).getTime();
    const recordTime = new Date(record.lastEventAt ?? record.startedAt ?? 0).getTime();
    if (record.totals.total_tokens > existing.totals.total_tokens || recordTime > existingTime) {
      sessions.set(key, record);
    }
  }
  return [...sessions.values()];
}

export class UsageStore {
  constructor(options = {}) {
    this.codexHome = options.codexHome ?? process.env.CODEX_HOME ?? path.join(os.homedir(), ".codex");
    this.cacheDir = options.cacheDir ?? path.join(os.homedir(), ".codex-meter");
    this.cachePath = path.join(this.cacheDir, "cache-v2.json");
    this.now = options.now ?? (() => new Date());
    this.files = new Map();
    this.quickRates = new Map();
    this.scanPromise = null;
    this.discoveredFiles = [];
    this.status = {
      phase: "starting",
      filesFound: 0,
      filesProcessed: 0,
      bytesFound: 0,
      bytesProcessed: 0,
      startedAt: null,
      completedAt: null,
      error: null,
    };
  }

  async initialize({ waitForScan = false } = {}) {
    await this.loadCache();
    this.discoveredFiles = await this.discoverFiles();
    await this.primeRecentRates(this.discoveredFiles);
    const scan = this.scan({ files: this.discoveredFiles });
    if (waitForScan) await scan;
    return this;
  }

  async discoverFiles() {
    const roots = [
      path.join(this.codexHome, "sessions"),
      path.join(this.codexHome, "archived_sessions"),
    ];
    const groups = await Promise.all(roots.map((root) => listJsonlFiles(root)));
    const files = [...new Set(groups.flat())];
    const detailed = [];
    for (const filePath of files) {
      try {
        const fileStat = await stat(filePath);
        detailed.push({ path: filePath, stat: fileStat });
      } catch {
        // A session can move to the archive while discovery is running.
      }
    }
    return detailed.sort((a, b) => b.stat.mtimeMs - a.stat.mtimeMs);
  }

  async loadCache() {
    try {
      const parsed = JSON.parse(await readFile(this.cachePath, "utf8"));
      if (parsed.version !== CACHE_VERSION || !parsed.files) return;
      for (const [filePath, record] of Object.entries(parsed.files)) {
        delete record.cwd;
        this.files.set(filePath, record);
      }
    } catch (error) {
      if (error.code !== "ENOENT" && !(error instanceof SyntaxError)) throw error;
    }
  }

  async saveCache() {
    await mkdir(this.cacheDir, { recursive: true });
    const files = Object.fromEntries(this.files);
    await writeFile(
      this.cachePath,
      JSON.stringify({ version: CACHE_VERSION, savedAt: new Date().toISOString(), files }),
      "utf8",
    );
  }

  async primeRecentRates(files) {
    for (const file of files.slice(0, RECENT_FILE_COUNT)) {
      try {
        const latest = latestRateFromTail(await readTail(file.path));
        if (!latest?.timestamp) continue;
        const limitId = latest.rateLimits.limit_id ?? "codex";
        const existing = this.quickRates.get(limitId);
        if (!existing || new Date(latest.timestamp) > new Date(existing.timestamp)) {
          this.quickRates.set(limitId, latest);
        }
      } catch {
        // The full scan will retry files that are changing right now.
      }
    }
  }

  async scan({ force = false, files = null } = {}) {
    if (this.scanPromise) return this.scanPromise;
    this.scanPromise = this.runScan({ force, files }).finally(() => {
      this.scanPromise = null;
    });
    return this.scanPromise;
  }

  async runScan({ force, files }) {
    try {
      const discovered = files ?? (await this.discoverFiles());
      this.discoveredFiles = discovered;
      const existingPaths = new Set(discovered.map((entry) => entry.path));
      for (const cachedPath of this.files.keys()) {
        if (!existingPaths.has(cachedPath)) this.files.delete(cachedPath);
      }

      this.status = {
        phase: "indexing",
        filesFound: discovered.length,
        filesProcessed: 0,
        bytesFound: discovered.reduce((sum, entry) => sum + entry.stat.size, 0),
        bytesProcessed: 0,
        startedAt: new Date().toISOString(),
        completedAt: null,
        error: null,
      };

      for (let index = 0; index < discovered.length; index += 1) {
        const entry = discovered[index];
        const cached = this.files.get(entry.path);
        const unchanged =
          !force &&
          cached &&
          cached.size === entry.stat.size &&
          cached.mtimeMs === entry.stat.mtimeMs;

        if (!unchanged) {
          const parsed = await parseFileAppend(entry.path, entry.stat, cached, force);
          this.files.set(entry.path, parsed);
        }

        this.status.filesProcessed += 1;
        this.status.bytesProcessed += entry.stat.size;
        if ((index + 1) % 25 === 0) await this.saveCache();
      }

      await this.saveCache();
      this.status.phase = "ready";
      this.status.completedAt = new Date().toISOString();
    } catch (error) {
      this.status.phase = "error";
      this.status.error = error instanceof Error ? error.message : String(error);
      this.status.completedAt = new Date().toISOString();
    }
  }

  getSnapshot() {
    const now = this.now();
    const records = newestBySession([...this.files.values()]);
    const allTime = emptyDay();
    const daysMap = {};
    const rateEntries = new Map(this.quickRates);
    let firstEventAt = null;
    let latestRatePayload = null;

    for (const record of records) {
      addTokens(allTime, record.totals);
      mergeModelTokens(allTime.models, record.byModel);
      allTime.requests += numberOrZero(record.requests);
      for (const [day, totals] of Object.entries(record.byDay ?? {})) {
        daysMap[day] ??= emptyDay();
        addDay(daysMap[day], totals);
      }

      const start = record.startedAt ?? record.lastEventAt;
      if (start && (!firstEventAt || new Date(start) < new Date(firstEventAt))) firstEventAt = start;

      if (record.latestRateLimits && record.latestRateTimestamp) {
        const limitId = record.latestRateLimits.limit_id ?? "codex";
        const existing = rateEntries.get(limitId);
        if (!existing || new Date(record.latestRateTimestamp) > new Date(existing.timestamp)) {
          rateEntries.set(limitId, {
            rateLimits: record.latestRateLimits,
            timestamp: record.latestRateTimestamp,
          });
        }
      }
    }

    for (const entry of rateEntries.values()) {
      if (!latestRatePayload || new Date(entry.timestamp) > new Date(latestRatePayload.timestamp)) {
        latestRatePayload = entry;
      }
    }

    const scanStatus = { ...this.status };
    scanStatus.percent = scanStatus.filesFound
      ? Math.round((scanStatus.filesProcessed / scanStatus.filesFound) * 100)
      : scanStatus.phase === "ready"
        ? 100
        : 0;

    return {
      generatedAt: now.toISOString(),
      source: {
        label: process.env.CODEX_HOME ? "Custom CODEX_HOME" : "~/.codex",
        localOnly: true,
        filesIndexed: records.length,
        firstEventAt,
        scan: scanStatus,
      },
      account: {
        plan: latestRatePayload?.rateLimits?.plan_type ?? null,
        credits: latestRatePayload?.rateLimits?.credits ?? null,
      },
      limits: flattenLimits([...rateEntries.values()], now),
      activity: {
        allTime,
        daily: allDailySeries(daysMap),
      },
      caveat:
        "Token totals are exact values recorded in local Codex session telemetry. Plan quota percentages are reported by Codex and may apply model or service-tier weighting that is not exposed as an absolute token allowance.",
    };
  }
}

export const testing = {
  emptyTokens,
  localDateKey,
  processTelemetryLine,
};
