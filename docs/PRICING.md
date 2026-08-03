# API-equivalent cost estimates

Codex Meter's dollar figure is a comparison tool, not a bill.

For every recorded model, the dashboard calculates:

```text
uncached input cost = (input tokens - cached input tokens) / 1,000,000 × input rate
cached input cost   = cached input tokens / 1,000,000 × cached-input rate
output cost         = output tokens / 1,000,000 × output rate
estimate            = uncached input cost + cached input cost + output cost
```

Reasoning output is a subset of output and is not added again.

## Automatic mode

Codex writes the active model ID in local `turn_context` telemetry. The collector attributes subsequent token-count deltas to that model. **Auto · recorded models** applies the corresponding rate to each model's token mix.

If a future or preview model has no listed API price, the UI discloses that it used the GPT-5.5 fallback. You can also select one model manually to compare the entire period at a single rate.

## What the estimate excludes

- ChatGPT subscription fees, included usage, credits, taxes, or invoices.
- Fast-mode and service-tier weighting.
- Tool-call fees, regional processing, and data-residency uplifts.
- Long-context price multipliers.
- Pricing changes after the table's verification date.

Rates in [`public/pricing.js`](../public/pricing.js) were verified on 14 July 2026 against OpenAI's [model comparison](https://developers.openai.com/api/docs/models/compare) and individual official model pages. Codex plan credit rates are documented separately in the [Codex rate card](https://help.openai.com/en/articles/20001106).
