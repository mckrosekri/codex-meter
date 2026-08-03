# Architecture

This repository has two local-first surfaces: the browser-based Codex Meter dashboard and the Windows Codex Decision client.

## Windows Codex Decision client

The WinUI 3 app uses the installed Codex CLI as its execution engine. It starts `codex app-server --stdio`, completes the JSON-RPC initialization handshake, and consumes the same thread, turn, model, plan, tool, command, file-change, diff, and approval protocol used by Codex clients.

- `windows/CodexDecision.Core/AppServer` owns the guarded process transport, reconnecting connection supervisor, typed App Server client, and local namespaced custom-agent configuration files.
- `windows/CodexDecision.Core/Routing` classifies each prompt, resolves model/reasoning/permission precedence, and builds adaptive phase-agent profiles.
- `windows/CodexDecision.Core/Conversations` owns per-task runtime snapshots, context pressure, queues, encrypted continuity, goals, conversation export, encrypted automation definitions, Work Receipts, and verification command execution.
- `windows/CodexDecision.Core/Projects` persists projects, task-to-thread mappings, managed-worktree metadata, execution profiles, checkpoint object IDs, and routing locks.
- `windows/CodexDecision.Core/Git` owns direct-argument Git execution, managed worktrees, safe handoff/cleanup, and guarded tracked-file checkpoints.
- `windows/CodexDecision.Core/SpecialSkills` owns stable special-skill IDs, display metadata, and normalization.
- `windows/CodexDecision.Core/Usage` ports the web meter's incremental telemetry index, cumulative-delta accounting, limit interpretation, billing cycles, pricing, aggregate-only cache, and preferences.
- `windows/CodexMeterTray/Views/ShellPage.xaml` renders question-only chats, optional projects, tasks, and the retained Settings and Usage entries. The navigation pane keeps only lightweight shell content so compact-pane transitions do not reparent interactive settings controls.
- `windows/CodexMeterTray/Views/ChatPage.xaml` renders the conversation, composer, routing controls, activity, approvals, and diff.
- `windows/CodexMeterTray/Views/MainPage.xaml` renders the exact local Usage dashboard with indexing progress, all current limit windows, historical cycles, recorded-model pricing, and token breakdowns.

Codex remains the source of truth for transcripts. The app reads thread history on demand and does not copy messages into its workspace store. Steering uses App Server `turn/steer` with the active turn id as a precondition. `%LOCALAPPDATA%\CodexDecision\workspace-v2.json` contains metadata and preferences only; a v1 file is migrated atomically and retained for rollback. Queues and goals remain process-local unless the user enables DPAPI Current User continuity, which writes encrypted payloads to `secure-queue-v1.json`. Scheduled definitions and bounded run summaries are separately DPAPI-encrypted in `automations-v1.json`; full results remain in their native Codex tasks. Work Receipts retain bounded operational evidence in DPAPI-encrypted `work-receipts.json` and deliberately omit prompts and assistant messages.

### Runtime and recovery

`TaskRuntimeCoordinator` is keyed by task ID and remains alive independently of page navigation. It routes native thread status/token notifications, tracks waiting/quiet/recovery states, and owns each follow-up queue. `CodexConnectionSupervisor` replaces failed App Server clients after 1, 3, and 10 seconds, never replays a request whose acceptance is uncertain, refreshes metadata, and resumes tracked threads. The Windows transport assigns only its own child process tree to a kill-on-close Job Object.

### Worktrees and checkpoints

Managed worktrees are created below `%LOCALAPPDATA%\CodexDecision\worktrees` with `ProcessStartInfo.ArgumentList`, start detached from a clean source repository, and validate every file action against the worktree root. Handoff requires clean Local and Worktree checkouts plus a named branch. Turn checkpoints use Git tree objects and restore tracked files only after verifying no newer tracked changes exist; untracked files are deliberately preserved.

Project Verification Gate commands are explicit opt-in policy. They run sequentially in the project's Native Windows or WSL execution profile with hidden, redirected processes, bounded output, per-command timeouts, and process-tree termination on timeout. Git evidence is captured after verification because checks can generate or format files. A SHA-256 fingerprint over HEAD, branch, and the sorted working-tree status makes stale receipts detectable before protected Git actions.

### Adaptive phase routing

On initialization, the app maps the currently reported model list to four namespaced Codex agent roles: explorer, implementation worker, verifier, and reviewer. It writes model/effort-only role configs under `%LOCALAPPDATA%\CodexDecision\adaptive-agents` and registers them through the App Server `config` object on both `thread/start` and `thread/resume`. When **Full Auto / Adaptive** is eligible, `turn/start` receives the original text, user-selected skills, and one internal adaptive-routing skill in the same request. That skill authorizes phase-sized native subagent delegation while keeping the parent agent responsible for coordination and integration.

Simple tasks, **Full Auto / One model**, and any non-automatic model or effort source disable adaptive delegation. Permission locks do not disable it because workers inherit the parent sandbox and approval policy. Collaboration items are rendered in the Activity pane with the model and effort actually requested for each spawned agent. The client does not interrupt turns or inject visible phase-continuation messages to simulate switching.

### Special-skill delivery

Bundled skills live under `windows/CodexMeterTray/SpecialSkills` and are copied into build and publish outputs. `AppServices` resolves selected IDs to absolute packaged paths. `CodexAppServerClient` preserves the user's original text as the first input and appends native `{ type: "skill", name, path }` inputs for both `turn/start` and `turn/steer`. Prompt Master raises only automatically classified simple work to the standard principal-engineering route; it does not alter permission detection or override one-turn, task, or project locks.

### Routing precedence

Each field is resolved independently:

1. One-turn selection
2. Task lock
3. Project lock
4. Full Auto

Full Auto selects among the models reported by `model/list`, chooses a supported reasoning effort, and resolves only to read-only or workspace-write. Full access is never automatic and requires explicit confirmation. If a locked model or effort is unavailable, the turn is blocked rather than silently changed. If the provider tries to reroute a locked model after start, the client interrupts the turn.

## Web usage dashboard

- `src/usage-store.js` incrementally scans local `.jsonl` sessions, retains aggregate token counts and model IDs, and writes the local cache.
- `src/server.js` serves the static UI and a localhost-only usage API.
- `public/cycles.js` groups activity into calendar months or user-configured subscription cycles.
- `public/pricing.js` calculates a labeled API-equivalent USD estimate.
- `public/app.js` renders the dashboard and stores UI preferences in browser local storage.

## Privacy boundary

The web server binds to loopback. The Windows client talks to a guarded child Codex App Server over redirected standard input/output. The workspace store, diagnostics, notifications, generated adaptive-agent configs, and usage cache never contain prompts or transcripts. Prompt-bearing continuity and automation files are encrypted with Windows DPAPI for the current user. User-initiated Markdown/JSON exports are the explicit exception and show a privacy warning. Tests and issue reports must use synthetic or redacted content.
