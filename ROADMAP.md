# Open Layer product roadmap

## Product promise

Open Layer is the local-first Codex control plane for developers who want Codex's native workflow with smarter routing, durable long-running work, and evidence they can trust before code leaves the machine.

The product should be sellable on three outcomes:

1. **Spend less attention and model budget** — route each task and subtask to the right model, effort, and permission level.
2. **Keep work moving safely** — queue, steer, schedule, recover, and continue long-running tasks without babysitting them.
3. **Know what actually happened** — preserve native Codex history while adding Git-aware, locally stored verification evidence.

## Feature map

### Core experience — available

- Native Codex projects, tasks, thread history, streaming, approvals, diffs, and tools.
- Seamless indexing of existing Codex projects and chats; no manual import flow.
- Full Auto routing with model, reasoning, and permission decisions plus persistent locks.
- Adaptive subtask routing so mechanical work does not consume the strongest model unnecessarily.
- Codex-style queue and steering, editable queued prompts, interruption, recovery, and task health.
- Goals, Prompt Master specialization, skills/plugins/MCP inventory, attachments, drag-and-drop, and native voice notes.
- Exact Codex usage windows, local Git review, branches, commit/push, checkpoints, and per-file undo.
- Scheduled follow-ups and monitors with attention classification.
- Local metadata and DPAPI-encrypted operational continuity; prompts and transcripts remain in Codex.

### Trust release — current priority

- **Work Receipt** after every completed interactive or scheduled turn.
- Route evidence: model, effort, permission, timing, and context at completion.
- Git evidence: branch, changed-file count, additions, deletions, and a working-tree fingerprint.
- **Verification Gate** with project-specific commands, timeouts, captured output, and Windows/WSL execution.
- Queue pause on failed verification.
- Optional current passing receipt requirement before commit or push.
- Rerun, inspect, and export a receipt without exporting the conversation.
- Encrypted local receipt history with bounded retention.

### Commercial readiness — next

#### P0: make the first 10 minutes excellent

- Setup health screen for Codex login, App Server, Git, shell/WSL, microphone, and project access.
- Suggested verification commands detected from solution/package/build files, always requiring user confirmation.
- One-click templates: .NET, Node, Python, mixed repository, read-only research, and scheduled monitor.
- Clear empty states and recovery actions for every external dependency.

#### P0: package trust for real teams

- Named policy presets for routing, permissions, verification, schedules, and Git protection.
- Receipt history/filter view with task, project, gate status, model, and date filters.
- Signed receipt bundle for pull-request or ticket attachment, with configurable path redaction.
- Policy drift indicator when a project differs from its chosen preset.

#### P1: remove coordination friction

- Notification destinations for Windows, Slack/Teams, and email through user-installed connectors.
- Issue handoff: create a task from GitHub/Linear and attach the final receipt back to the work item.
- Scheduled monitor dashboard with last success, next run, failure streak, and snooze.
- Reusable task recipes combining a goal template, skills, route policy, verification, and schedule.

#### P1: make routing value visible

- Decision explanation timeline: why the model/effort/permission changed and which lock or policy won.
- Estimated avoided premium-model usage, clearly labeled as an estimate rather than billing data.
- Routing replay against historical metadata to compare policy changes without resending prompts.
- Per-project reliability and verification trends.

#### P1: ship like a paid Windows product

- Signed installer, safe auto-update channel, release notes, rollback, and install diagnostics.
- Opt-in crash reporting with local preview/redaction before upload.
- License and entitlement layer that never blocks access to local task history.
- Backup/export/import for Open Layer metadata, policies, schedules, and encrypted receipts.

#### P2: defensible platform

- Public extension contract for special skills, policy packs, verification detectors, and receipt enrichers.
- Organization policy distribution with local enforcement and transparent precedence.
- Cross-machine encrypted sync as a separate opt-in service.
- Team analytics based on receipt metadata, never prompt or transcript ingestion by default.

## Delivery sequence

| Milestone | Customer proof | Exit criteria |
| --- | --- | --- |
| Trust release | “I can let it work and prove what passed.” | Receipt/gate tests pass; failed gates pause queues; stale receipts block protected Git actions; wide and narrow UI verified. |
| Onboarding release | “I can install it and succeed without support.” | A clean Windows VM reaches a verified first task in under 10 minutes. |
| Team release | “We can standardize it without losing local control.” | Presets, receipt history, redaction, and issue/notification handoff work end to end. |
| Paid release | “This behaves like software I can buy for work.” | Signed updates, diagnostics, licensing, backup, privacy copy, and support runbook are complete. |
| Platform release | “Our workflow can extend it safely.” | Versioned extension API, permission boundaries, examples, and compatibility tests are published. |

## Product rules

- Preserve native Codex as the source of truth for conversations; do not duplicate transcripts into routing, receipt, or analytics stores.
- Distinguish exact Codex usage from estimated savings everywhere.
- Never run discovered or imported commands without explicit project-level opt-in.
- A receipt is deterministic evidence, not a security guarantee.
- Local control remains useful without an Open Layer cloud account.
- Sell reliability and control before adding a marketplace or social layer.
