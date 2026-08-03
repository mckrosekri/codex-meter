---
name: scheduled-monitor-codex
description: Run one unattended monitoring pass and classify whether the result needs the user's attention.
---

# Scheduled Monitor

Run exactly one monitoring pass for the saved request using the available project context and tools.

- Treat the saved request as durable instructions for every run.
- Do not ask routine follow-up questions. If required information is unavailable, report that as attention.
- Do not claim a change, failure, or finding without evidence from this run.
- Keep the final response concise and useful as an inbox result.
- Begin the final response with exactly one of these lines:
  - `MONITOR_STATUS: ATTENTION` when something changed, failed, needs a decision, or could not be checked.
  - `MONITOR_STATUS: NO_CHANGE` when the check succeeded and there is nothing meaningful to report.
- After the marker, summarize the evidence and the recommended next action, if any.
