---
name: adaptive-routing-codex
description: Route bounded phases of a long Codex task to registered model-specialized agents while the main agent remains the coordinator.
---

# Adaptive phase routing

This skill is attached only when the user has enabled **Full Auto · Adaptive**. The user has explicitly authorized useful model-specialized delegation for this turn.

Keep the main agent responsible for the overall objective, decisions, permission boundary, integration, and final response. Delegate only a meaningful bounded phase when doing so should reduce cost or latency without lowering quality:

- Use `decision_explorer` for read-heavy repository exploration, targeted file searches, logs, data gathering, or evidence summaries.
- Use `decision_worker` for a clearly specified implementation phase or mechanical fix after requirements and boundaries are known.
- Use `decision_verifier` for focused builds, tests, deterministic result extraction, or failure triage.
- Use `decision_reviewer` for high-judgment correctness, architecture, security, edge-case, or completion review.

Apply these orchestration rules:

1. Use collaboration/subagent tools and select the registered role that matches the phase. Its configured model and reasoning effort are authoritative.
2. Hand off phase-sized work, not individual shell commands or single file reads. Do not delegate tiny tasks where setup and context transfer cost more than the work.
3. Prefer sequential handoffs when one phase depends on another. Use parallel agents only for genuinely independent work.
4. Give each worker the objective, bounded scope, relevant paths, constraints, permission expectations, and a clear completion contract. Do not send unrelated conversation history.
5. Keep writes coordinated. Avoid concurrent edits to overlapping files, preserve unrelated user changes, and make the main agent responsible for integration.
6. Require workers to return concise evidence and summaries instead of raw logs. Escalate ambiguity, repeated failure, security-sensitive decisions, and architectural tradeoffs back to the main agent.
7. If a registered role or alternate model is unavailable, continue safely with the current agent. Do not invent a role, silently weaken a user lock, or loop on failed handoffs.
8. Finish the complete user task. Delegation is an internal execution detail, not a reason to stop after planning or exploration.
