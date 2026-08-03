const TOKEN_KEYS = [
  "input_tokens",
  "cached_input_tokens",
  "output_tokens",
  "reasoning_output_tokens",
  "total_tokens",
];

function emptyTotals() {
  return {
    input_tokens: 0,
    cached_input_tokens: 0,
    output_tokens: 0,
    reasoning_output_tokens: 0,
    total_tokens: 0,
    requests: 0,
    models: {},
  };
}

function addTotals(target, source) {
  for (const key of TOKEN_KEYS) target[key] += Number(source?.[key]) || 0;
  target.requests += Number(source?.requests) || 0;
  for (const [model, totals] of Object.entries(source?.models ?? {})) {
    target.models[model] ??= Object.fromEntries(TOKEN_KEYS.map((key) => [key, 0]));
    for (const key of TOKEN_KEYS) {
      target.models[model][key] += Number(totals?.[key]) || 0;
    }
  }
}

function parseDateKey(key) {
  const [year, month, day] = String(key).split("-").map(Number);
  if (!year || !month || !day) return null;
  const date = new Date(year, month - 1, day, 12);
  if (
    date.getFullYear() !== year ||
    date.getMonth() !== month - 1 ||
    date.getDate() !== day
  ) {
    return null;
  }
  return date;
}

function dateKey(date) {
  const year = date.getFullYear();
  const month = String(date.getMonth() + 1).padStart(2, "0");
  const day = String(date.getDate()).padStart(2, "0");
  return `${year}-${month}-${day}`;
}

function daysInMonth(year, monthIndex) {
  return new Date(year, monthIndex + 1, 0, 12).getDate();
}

function cycleStartForDate(date, billingDay) {
  const start = new Date(date.getFullYear(), date.getMonth(), 1, 12);
  const effectiveDay = Math.min(
    billingDay,
    daysInMonth(start.getFullYear(), start.getMonth()),
  );

  if (date.getDate() < effectiveDay) start.setMonth(start.getMonth() - 1);
  start.setDate(Math.min(billingDay, daysInMonth(start.getFullYear(), start.getMonth())));
  return start;
}

function nextCycleStart(start, billingDay) {
  const next = new Date(start.getFullYear(), start.getMonth() + 1, 1, 12);
  next.setDate(Math.min(billingDay, daysInMonth(next.getFullYear(), next.getMonth())));
  return next;
}

function cycleBounds(date, billingDay) {
  const start = cycleStartForDate(date, billingDay);
  const next = nextCycleStart(start, billingDay);
  const end = new Date(next);
  end.setDate(end.getDate() - 1);
  return {
    key: dateKey(start),
    start,
    end,
    startDate: dateKey(start),
    endDate: dateKey(end),
  };
}

function dailySeries(start, end, values) {
  const output = [];
  const cursor = new Date(start);
  while (cursor <= end) {
    const key = dateKey(cursor);
    output.push({ date: key, ...(values.get(key) ?? emptyTotals()) });
    cursor.setDate(cursor.getDate() + 1);
  }
  return output;
}

export function normalizeBillingDay(value) {
  if (value === null || value === undefined || value === "") return null;
  const day = Number(value);
  return Number.isInteger(day) && day >= 1 && day <= 31 ? day : null;
}

export function ordinal(day) {
  const mod100 = day % 100;
  if (mod100 >= 11 && mod100 <= 13) return `${day}th`;
  if (day % 10 === 1) return `${day}st`;
  if (day % 10 === 2) return `${day}nd`;
  if (day % 10 === 3) return `${day}rd`;
  return `${day}th`;
}

export function buildUsageCycles(daily, billingDayValue, nowValue = new Date()) {
  const billingDay = normalizeBillingDay(billingDayValue);
  const effectiveDay = billingDay ?? 1;
  const now = new Date(nowValue);
  now.setHours(12, 0, 0, 0);
  const currentBounds = cycleBounds(now, effectiveDay);
  const values = new Map();

  for (const item of Array.isArray(daily) ? daily : []) {
    if (!parseDateKey(item.date)) continue;
    const existing = values.get(item.date) ?? emptyTotals();
    addTotals(existing, item);
    values.set(item.date, existing);
  }

  const cycleMap = new Map();
  for (const [key, totals] of values) {
    const date = parseDateKey(key);
    const bounds = cycleBounds(date, effectiveDay);
    const cycle = cycleMap.get(bounds.key) ?? {
      ...bounds,
      totals: emptyTotals(),
      values: new Map(),
    };
    addTotals(cycle.totals, totals);
    cycle.values.set(key, totals);
    cycleMap.set(bounds.key, cycle);
  }

  if (!cycleMap.has(currentBounds.key)) {
    cycleMap.set(currentBounds.key, {
      ...currentBounds,
      totals: emptyTotals(),
      values: new Map(),
    });
  }

  const earliestKey = [...cycleMap.keys()].sort()[0];
  let cursorBounds = currentBounds;
  while (cursorBounds.key > earliestKey) {
    const previousDate = new Date(cursorBounds.start);
    previousDate.setDate(previousDate.getDate() - 1);
    cursorBounds = cycleBounds(previousDate, effectiveDay);
    if (!cycleMap.has(cursorBounds.key)) {
      cycleMap.set(cursorBounds.key, {
        ...cursorBounds,
        totals: emptyTotals(),
        values: new Map(),
      });
    }
  }

  const cycles = [...cycleMap.values()]
    .sort((left, right) => right.key.localeCompare(left.key))
    .map((cycle) => {
      const isCurrent = cycle.key === currentBounds.key;
      const chartEnd = isCurrent && now < cycle.end ? now : cycle.end;
      return {
        key: cycle.key,
        startDate: cycle.startDate,
        endDate: cycle.endDate,
        isCurrent,
        totals: cycle.totals,
        daily: dailySeries(cycle.start, chartEnd, cycle.values),
      };
    });

  return {
    mode: billingDay ? "subscription" : "calendar",
    billingDay,
    currentCycleKey: currentBounds.key,
    cycles,
  };
}
