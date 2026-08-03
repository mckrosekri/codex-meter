import { buildUsageCycles, normalizeBillingDay, ordinal } from "./cycles.js";
import {
  estimateApiCost,
  normalizePricingBasis,
  PRICING_MODELS,
} from "./pricing.js";

const elements = {
  activityScope: document.querySelector("#activity-scope"),
  billingDay: document.querySelector("#billing-day-select"),
  breakdown: document.querySelector("#breakdown"),
  chart: document.querySelector("#activity-chart"),
  chartTotal: document.querySelector("#chart-total"),
  connection: document.querySelector("#connection-status"),
  connectionLabel: document.querySelector("#connection-label"),
  costContext: document.querySelector("#cost-context"),
  costValue: document.querySelector("#cost-value"),
  cycleSelect: document.querySelector("#cycle-select"),
  historySummary: document.querySelector("#history-summary"),
  indexing: document.querySelector("#indexing"),
  indexingBar: document.querySelector("#indexing-bar"),
  indexingDetail: document.querySelector("#indexing-detail"),
  indexingTitle: document.querySelector("#indexing-title"),
  limits: document.querySelector("#limits"),
  metrics: document.querySelector("#metric-strip"),
  planChip: document.querySelector("#plan-chip"),
  pricingBasis: document.querySelector("#pricing-basis-select"),
  refresh: document.querySelector("#refresh-button"),
  sourceLabel: document.querySelector("#source-label"),
  toast: document.querySelector("#toast"),
  updatedAt: document.querySelector("#updated-at"),
};

const numberFormatter = new Intl.NumberFormat(undefined, { maximumFractionDigits: 0 });
const dateTimeFormatter = new Intl.DateTimeFormat(undefined, {
  dateStyle: "medium",
  timeStyle: "short",
});
const shortDateFormatter = new Intl.DateTimeFormat(undefined, {
  month: "short",
  day: "numeric",
});
const monthFormatter = new Intl.DateTimeFormat(undefined, {
  month: "long",
  year: "numeric",
});
const cycleStartFormatter = new Intl.DateTimeFormat(undefined, {
  month: "short",
  day: "numeric",
});
const cycleEndFormatter = new Intl.DateTimeFormat(undefined, {
  month: "short",
  day: "numeric",
  year: "numeric",
});
const relativeFormatter = new Intl.RelativeTimeFormat(undefined, { numeric: "auto" });
const currencyFormatter = new Intl.NumberFormat(undefined, {
  style: "currency",
  currency: "USD",
  currencyDisplay: "narrowSymbol",
  minimumFractionDigits: 2,
  maximumFractionDigits: 2,
});

let snapshot = null;
let billingDay = loadBillingDay();
let pricingBasis = loadPricingBasis();
let selectedCycleKey = null;
let pollTimer = null;
let toastTimer = null;

function formatNumber(value) {
  return numberFormatter.format(Number(value) || 0);
}

function formatPercent(value) {
  return `${(Number(value) || 0).toFixed(1)}%`;
}

function relativeTime(dateValue) {
  if (!dateValue) return "Reset time unavailable";
  const difference = new Date(dateValue).getTime() - Date.now();
  const absolute = Math.abs(difference);
  if (absolute >= 86_400_000) return relativeFormatter.format(Math.round(difference / 86_400_000), "day");
  if (absolute >= 3_600_000) return relativeFormatter.format(Math.round(difference / 3_600_000), "hour");
  return relativeFormatter.format(Math.round(difference / 60_000), "minute");
}

function windowLabel(minutes) {
  if (!minutes) return "Window unavailable";
  if (minutes % 1440 === 0) return `${minutes / 1440}-day window`;
  if (minutes % 60 === 0) return `${minutes / 60}-hour window`;
  return `${minutes}-minute window`;
}

function createElement(tag, className, text) {
  const element = document.createElement(tag);
  if (className) element.className = className;
  if (text !== undefined) element.textContent = text;
  return element;
}

function loadBillingDay() {
  try {
    return normalizeBillingDay(localStorage.getItem("codex-meter.billing-day"));
  } catch {
    return null;
  }
}

function saveBillingDay(day) {
  try {
    if (day) localStorage.setItem("codex-meter.billing-day", String(day));
    else localStorage.removeItem("codex-meter.billing-day");
  } catch {
    // The setting remains active for this tab when browser storage is unavailable.
  }
}

function loadPricingBasis() {
  try {
    return normalizePricingBasis(localStorage.getItem("codex-meter.pricing-basis") ?? "auto");
  } catch {
    return "auto";
  }
}

function savePricingBasis(value) {
  try {
    localStorage.setItem("codex-meter.pricing-basis", value);
  } catch {
    // The setting remains active for this tab when browser storage is unavailable.
  }
}

function dateFromKey(key) {
  return new Date(`${key}T12:00:00`);
}

function cycleLabel(cycle, mode) {
  if (mode === "calendar") return monthFormatter.format(dateFromKey(cycle.startDate));
  return `${cycleStartFormatter.format(dateFromKey(cycle.startDate))} – ${cycleEndFormatter.format(dateFromKey(cycle.endDate))}`;
}

function initializeBillingDays() {
  for (let day = 1; day <= 31; day += 1) {
    const option = createElement("option", "", ordinal(day));
    option.value = String(day);
    elements.billingDay.append(option);
  }
  elements.billingDay.value = billingDay ? String(billingDay) : "";
}

function initializePricingBasis() {
  const automatic = createElement("option", "", "Auto · recorded models");
  automatic.value = "auto";
  const options = PRICING_MODELS.map((model) => {
    const option = createElement("option", "", model.label);
    option.value = model.id;
    return option;
  });
  elements.pricingBasis.replaceChildren(automatic, ...options);
  elements.pricingBasis.value = pricingBasis;
}

function renderLimits(limits) {
  elements.limits.replaceChildren();
  if (!limits.length) {
    const empty = createElement("div", "limit-empty");
    empty.append(
      createElement("strong", "", "No current limit event found"),
      createElement(
        "p",
        "",
        "Run a Codex task, then refresh. Codex records the active windows after a model response.",
      ),
    );
    elements.limits.append(empty);
    return;
  }

  for (const limit of limits) {
    const row = createElement("article", "limit-row");
    row.dataset.state = limit.state;

    const name = createElement("div", "limit-name");
    name.append(createElement("span", "limit-state"));
    const nameCopy = createElement("div");
    nameCopy.append(
      createElement("strong", "", limit.name),
      createElement("span", "", windowLabel(limit.windowMinutes)),
    );
    name.append(nameCopy);

    const progress = createElement("div", "limit-progress");
    const progressCopy = createElement("div", "limit-progress-copy");
    progressCopy.append(
      createElement("strong", "", `${formatPercent(limit.usedPercent)} used`),
      createElement("span", "", `${formatPercent(limit.remainingPercent)} remaining`),
    );
    const track = createElement("div", "progress-track");
    track.setAttribute("role", "progressbar");
    track.setAttribute("aria-label", `${limit.name} used`);
    track.setAttribute("aria-valuemin", "0");
    track.setAttribute("aria-valuemax", "100");
    track.setAttribute("aria-valuenow", String(limit.usedPercent));
    const fill = createElement("span");
    fill.style.width = `${Math.min(100, Math.max(0, limit.usedPercent))}%`;
    track.append(fill);
    progress.append(progressCopy, track);

    const reset = createElement("div", "limit-reset");
    if (limit.resetsAt) {
      const time = createElement("time", "", `Resets ${relativeTime(limit.resetsAt)}`);
      time.dateTime = limit.resetsAt;
      time.title = dateTimeFormatter.format(new Date(limit.resetsAt));
      reset.append(time, createElement("span", "", dateTimeFormatter.format(new Date(limit.resetsAt))));
    } else {
      reset.append(createElement("strong", "", "Reset unavailable"));
    }

    row.append(name, progress, reset);
    elements.limits.append(row);
  }
}

function renderMetrics(range) {
  const values = [
    ["Total tokens", range.total_tokens],
    ["Input", range.input_tokens],
    ["Output", range.output_tokens],
    ["Model responses", range.requests],
  ];
  const metrics = values.map(([label, value]) => {
    const item = createElement("div");
    item.append(createElement("span", "", label), createElement("strong", "", formatNumber(value)));
    return item;
  });
  elements.metrics.replaceChildren(...metrics);
}

function renderBreakdown(range) {
  const values = [
    ["Uncached input", Math.max(0, range.input_tokens - range.cached_input_tokens), false],
    ["Cached input", range.cached_input_tokens, false],
    ["Output", range.output_tokens, false],
    ["Reasoning output", range.reasoning_output_tokens, true],
  ];
  const rows = values.map(([label, value, subset]) => {
    const row = createElement("div");
    const term = createElement("dt", "", label);
    if (subset) term.append(" ", createElement("small", "", "subset"));
    row.append(term, createElement("dd", "", formatNumber(value)));
    return row;
  });
  elements.breakdown.replaceChildren(...rows);
}

function renderCostEstimate(range) {
  const estimate = estimateApiCost(range, pricingBasis);
  elements.costValue.textContent = currencyFormatter.format(estimate.cost.total);

  if (estimate.basis !== "auto") {
    elements.costContext.textContent = `Using ${estimate.label} standard API rates.`;
    return;
  }

  const count = estimate.recordedModels;
  const modelText = `${count} recorded model${count === 1 ? "" : "s"}`;
  elements.costContext.textContent = estimate.fallbackModels.length
    ? `${modelText} · ${estimate.fallbackModels.length} unpriced model${estimate.fallbackModels.length === 1 ? "" : "s"} used the GPT-5.5 fallback.`
    : `Automatically priced from ${modelText}.`;
}

function renderChart(days, label) {
  elements.chart.replaceChildren();
  elements.chart.style.setProperty("--chart-columns", days.length);
  const maximum = Math.max(...days.map((day) => day.total_tokens), 0);
  const total = days.reduce((sum, day) => sum + day.total_tokens, 0);
  elements.chartTotal.textContent = `${formatNumber(total)} total`;
  elements.chart.setAttribute(
    "aria-label",
    `${label} token usage chart. ${formatNumber(total)} total tokens.`,
  );

  if (maximum === 0) {
    elements.chart.append(createElement("div", "chart-empty", "No recorded token activity in this period."));
    return;
  }

  days.forEach((day, index) => {
    const wrap = createElement("div", "chart-bar-wrap");
    const height = day.total_tokens === 0 ? 1 : Math.max(3, (day.total_tokens / maximum) * 100);
    wrap.style.setProperty("--bar-height", `${height}%`);

    const bar = createElement("div", "chart-bar");
    bar.tabIndex = 0;
    bar.setAttribute("role", "img");
    bar.style.transform = `scaleY(${height / 100})`;
    const date = new Date(`${day.date}T12:00:00`);
    const accessibleDate = date.toLocaleDateString(undefined, { dateStyle: "full" });
    bar.setAttribute(
      "aria-label",
      `${accessibleDate}: ${formatNumber(day.total_tokens)} tokens, ${formatNumber(day.requests)} model responses`,
    );

    const tooltip = createElement(
      "span",
      "chart-tooltip",
      `${formatNumber(day.total_tokens)} tokens · ${formatNumber(day.requests)} responses`,
    );
    wrap.append(bar, tooltip);

    const labelEvery = days.length <= 7 ? 1 : days.length <= 14 ? 2 : 5;
    if (index % labelEvery === 0 || index === days.length - 1) {
      wrap.append(createElement("span", "chart-bar-label", shortDateFormatter.format(date)));
    }
    elements.chart.append(wrap);
  });
}

function renderActivity() {
  if (!snapshot) return;
  const model = buildUsageCycles(snapshot.activity.daily, billingDay, snapshot.generatedAt);
  if (!model.cycles.some((cycle) => cycle.key === selectedCycleKey)) {
    selectedCycleKey = model.currentCycleKey;
  }
  const cycle =
    model.cycles.find((candidate) => candidate.key === selectedCycleKey) ?? model.cycles[0];
  if (!cycle) return;

  const options = model.cycles.map((candidate) => {
    const suffix = candidate.isCurrent ? " · current" : "";
    const option = createElement("option", "", `${cycleLabel(candidate, model.mode)}${suffix}`);
    option.value = candidate.key;
    return option;
  });
  elements.cycleSelect.replaceChildren(...options);
  elements.cycleSelect.value = cycle.key;
  elements.billingDay.value = billingDay ? String(billingDay) : "";
  elements.activityScope.textContent = billingDay
    ? `Subscription-cycle totals · renews on the ${ordinal(billingDay)}.`
    : "Calendar-month totals · set your renewal day to match your subscription cycle.";

  const label = cycleLabel(cycle, model.mode);
  renderMetrics(cycle.totals);
  renderBreakdown(cycle.totals);
  renderCostEstimate(cycle.totals);
  renderChart(cycle.daily, label);
}

function renderScanStatus(source) {
  const scan = source.scan;
  const indexing = scan.phase === "indexing" || scan.phase === "starting";
  elements.indexing.hidden = !indexing;
  elements.connection.dataset.state = scan.phase === "error" ? "error" : scan.phase === "ready" ? "ready" : "indexing";
  elements.connectionLabel.textContent =
    scan.phase === "error" ? "Index error" : scan.phase === "ready" ? "Local · ready" : "Local · indexing";

  if (indexing) {
    elements.indexingTitle.textContent = `Indexing local history · ${scan.percent}%`;
    elements.indexingDetail.textContent = `${formatNumber(scan.filesProcessed)} of ${formatNumber(scan.filesFound)} session files. Current limits are already available.`;
    elements.indexingBar.style.transform = `scaleX(${scan.percent / 100})`;
  }

  if (scan.phase === "error") {
    elements.indexing.hidden = false;
    elements.indexingTitle.textContent = "History index stopped";
    elements.indexingDetail.textContent = scan.error || "An unknown indexing error occurred.";
    elements.indexingBar.style.transform = "scaleX(1)";
  }
}

function renderSnapshot(data) {
  snapshot = data;
  renderScanStatus(data.source);
  renderLimits(data.limits);
  renderActivity();

  elements.sourceLabel.textContent = data.source.label;
  elements.updatedAt.textContent = `Updated ${relativeTime(data.generatedAt)} · ${formatNumber(data.activity.allTime.total_tokens)} tokens indexed in total`;

  if (data.account.plan) {
    elements.planChip.hidden = false;
    elements.planChip.textContent = `${data.account.plan} plan`;
  } else {
    elements.planChip.hidden = true;
  }

  if (data.source.firstEventAt) {
    elements.historySummary.textContent = `${formatNumber(data.source.filesIndexed)} sessions · history since ${shortDateFormatter.format(new Date(data.source.firstEventAt))}`;
  } else {
    elements.historySummary.textContent = `${formatNumber(data.source.filesIndexed)} sessions indexed`;
  }
}

function showToast(message) {
  clearTimeout(toastTimer);
  elements.toast.textContent = message;
  elements.toast.hidden = false;
  toastTimer = setTimeout(() => {
    elements.toast.hidden = true;
  }, 2800);
}

async function loadUsage({ announce = false } = {}) {
  clearTimeout(pollTimer);
  try {
    const response = await fetch("/api/usage", { cache: "no-store" });
    if (!response.ok) throw new Error(`Usage request failed (${response.status})`);
    const data = await response.json();
    renderSnapshot(data);
    if (announce) showToast("Usage refreshed");
    pollTimer = setTimeout(loadUsage, data.source.scan.phase === "indexing" ? 1500 : 20_000);
  } catch (error) {
    elements.connection.dataset.state = "error";
    elements.connectionLabel.textContent = "Disconnected";
    elements.updatedAt.textContent = "Could not reach the local collector.";
    if (!snapshot) {
      const state = createElement("div", "error-state");
      state.append(
        createElement("strong", "", "Collector unavailable"),
        createElement("p", "", "Check that the local server is running, then refresh this page."),
      );
      elements.limits.replaceChildren(state);
    }
    pollTimer = setTimeout(loadUsage, 5000);
  }
}

async function requestRefresh() {
  elements.refresh.disabled = true;
  elements.refresh.dataset.loading = "true";
  try {
    const response = await fetch("/api/rescan", {
      method: "POST",
      headers: { "X-Codex-Meter": "1" },
    });
    if (!response.ok) throw new Error(`Refresh failed (${response.status})`);
    await loadUsage({ announce: true });
  } catch {
    showToast("Refresh failed. The collector may be offline.");
  } finally {
    elements.refresh.disabled = false;
    elements.refresh.dataset.loading = "false";
  }
}

elements.billingDay.addEventListener("change", () => {
  billingDay = normalizeBillingDay(elements.billingDay.value);
  saveBillingDay(billingDay);
  selectedCycleKey = null;
  renderActivity();
});

elements.cycleSelect.addEventListener("change", () => {
  selectedCycleKey = elements.cycleSelect.value;
  renderActivity();
});

elements.pricingBasis.addEventListener("change", () => {
  pricingBasis = normalizePricingBasis(elements.pricingBasis.value);
  savePricingBasis(pricingBasis);
  renderActivity();
});

elements.refresh.addEventListener("click", requestRefresh);
initializeBillingDays();
initializePricingBasis();
loadUsage();
