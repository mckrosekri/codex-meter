# Codex Meter + Codex Decision

A local-first Codex toolkit: an exact usage dashboard plus a Windows project/task/chat client with automatic model, reasoning, and permission routing.

This is an independent source-available project. Noncommercial use, modification, and distribution are available under the [PolyForm Noncommercial License 1.0.0](LICENSE). Commercial use—including selling the system or offering it as a paid service—requires a separate written license from `mckrosekri`. Read [Licensing](LICENSING.md) for the paid-use model, contributor rights, historical boundary, and third-party terms. The bundled Prompt Master attribution remains in [`NOTICE.md`](windows/CodexMeterTray/SpecialSkills/PromptMasterCodex/NOTICE.md).

**Project guides:** [Set up and continue development](DEVELOPMENT.md) · [Architecture](docs/ARCHITECTURE.md) · [Extension points](docs/EXTENDING.md) · [Licensing and commercial use](LICENSING.md) · [Contributing](CONTRIBUTING.md) · [Security](SECURITY.md) · [Code of conduct](CODE_OF_CONDUCT.md) · [Roadmap](ROADMAP.md)

## Why this exists

Codex's organization analytics are useful for aggregated workspace reporting, but they are not a focused personal limit meter. Codex Meter reads the `token_count` telemetry already stored by local Codex sessions and turns it into a small, understandable dashboard.

It shows:

- Current rate-limit windows, used percentage, remaining percentage, and reset time.
- Exact locally recorded input, cached-input, output, reasoning-output, and total tokens.
- Monthly token activity, separated into calendar months until a subscription renewal day is configured.
- Subscription-cycle history once a renewal day is set, with each cycle starting from zero.
- A model-aware USD estimate using standard OpenAI API token rates.
- Background indexing progress for large histories.

It deliberately does not show prompts, transcripts, vanity scores, or account analytics unrelated to your limits.

## Run it

Requirements: Node.js 20.11 or newer and a local Codex installation.

```bash
git clone https://github.com/mckrosekri/codex-meter.git
cd codex-meter
npm start
```

Open [http://127.0.0.1:4317](http://127.0.0.1:4317).

If Windows PowerShell blocks the `npm.ps1` shim, use `npm.cmd start` or `node src/server.js` instead.

No package install, API key, or login is required. The first historical index can take a little while for very large Codex archives; current limits are read immediately and the local cache makes later starts incremental.

### Codex Decision Windows app

The Windows app now combines the exact local usage meter with a Codex-style project/task/chat client backed by the installed Codex App Server. Its Usage page shares the web dashboard's cumulative-delta indexing, model attribution, billing cycles, pricing basis, and complete set of active rate-limit windows. It streams conversations, plans, tools, commands, approvals, and diffs while a decision layer chooses the model, reasoning effort, and safe permission mode.

The composer stays available while Codex works. Follow-up messages can either steer the active turn or enter a visible multi-message queue for later turns; queued messages can be edited, reordered, sent immediately, or deleted. Each queued turn is routed independently when it starts, while task/project locks and captured one-turn choices keep their normal precedence. Ctrl+Shift+Enter temporarily inverts the saved Queue/Steer behavior.

The model selector offers **Full Auto / Adaptive** and **Full Auto / One model**. Adaptive mode keeps the initial model as coordinator and lets Codex hand bounded exploration, implementation, verification, and review phases to locally registered model-specialized agents. Narrow tasks stay on one model to avoid handoff overhead, and any explicit model or reasoning selection or lock suspends adaptive handoffs. The Activity pane shows the configured phase routes and actual model handoffs. This uses Codex's native subagents rather than a separate preflight model call; a subagent consumes its own usage only when Codex actually delegates useful work.

Long-running tasks now keep their thread, turn, queue, context pressure, and recovery state in a shared runtime coordinator. The App Server connection is supervised and restarted after unexpected EOF/process failures without replaying uncertain requests, and app-owned processes are attached to a Windows kill-on-close job. Background notifications cover approvals, questions, completion, and failures. Context usage, manual compaction, conversation branching, editable fresh-continuation handoffs, encrypted Goal mode, local attachments, transcript export, and tracked-file turn undo/redo are available from the chat surface.

**New task** starts a normal question-only chat in a private local workspace, so a repository is never required just to ask Codex something. **Add project** is optional; project tasks can run in the local checkout or a clean, detached managed Git worktree. Worktree review includes diffs, stage/unstage/discard, branch creation, commit, guarded handoff to Local, and confirmed cleanup. Projects support Native Windows or WSL execution profiles. The sidebar includes task search, pin/archive/rename actions, health badges, encrypted scheduled monitors and follow-ups with a run inbox, and native Codex skill/plugin/MCP inventory.

Every completed interactive or scheduled turn produces an encrypted local **Work Receipt** with route, permission, timing, context-at-completion, and Git evidence. Projects can opt into a **Verification Gate** that runs explicit Windows or WSL commands after each successful turn, captures bounded command output, pauses queued work when checks fail, and can require a current passing receipt before commit or push. Receipts can be inspected, rerun, and exported without exporting the conversation.

The **Settings** page contains an opt-in **Prompt Master** specialization for OpenAI Codex. It keeps the user's visible message unchanged while attaching a native Codex skill that interprets informal wording as a precise principal-engineering brief. Normal sends and steering use the current toggle; queued messages retain the skill selection captured when they were queued. The specialization is adapted from the MIT-licensed [nidhinjs/prompt-master](https://github.com/nidhinjs/prompt-master) project.

Manual controls remain available: a one-turn choice can override Full Auto, and the adjacent lock menu can persist a route for the current task or project. Permission modes are Auto safe, read-only, workspace-write, and explicitly confirmed full access. Full Auto never chooses full access, and an unavailable locked model is never silently replaced.

Download the per-user installer from the [latest GitHub release](https://github.com/mckrosekri/codex-meter/releases/latest), or see [Windows installation and build instructions](docs/WINDOWS.md). The installer offers an optional, unchecked **Start Codex Decision when I sign in** task. Early unsigned builds can show an unknown-publisher warning.

Codex telemetry does not include your ChatGPT subscription renewal date. Set the renewal day in either dashboard if you want token history grouped by subscription cycle. The web setting stays in browser storage and the Windows setting stays in `%LOCALAPPDATA%\CodexDecision`; leaving it unset uses clearly labeled calendar months.

### Cost estimate

The dashboard attributes each token event to the active model recorded by Codex and prices uncached input, cached input, and output independently. Use **Auto · recorded models** for that model mix, or select one model to compare the whole period at a single rate.

The result is an **API-equivalent estimate in USD**, not a ChatGPT subscription charge or invoice. It uses standard API token rates and does not include fast-mode weighting, tool-call fees, regional processing, long-context uplifts, or future pricing changes. Reasoning output is already included in output tokens and is never charged twice. Rates were verified on 14 July 2026; see the [official model comparison](https://developers.openai.com/api/docs/models/compare).

## Configuration

| Environment variable | Default | Purpose |
| --- | --- | --- |
| `CODEX_HOME` | `~/.codex` | Read telemetry from a custom Codex home directory. |
| `HOST` | `127.0.0.1` | Server bind address. Keep this local unless you add authentication. |
| `PORT` | `4317` | Dashboard port. |

PowerShell example:

```powershell
$env:PORT = "8080"
npm start
```

## Privacy and security

- The server binds to `127.0.0.1` by default.
- The browser talks only to the local server.
- The collector parses `session_meta`, `turn_context`, and `token_count` events. From `turn_context` it retains only the model ID; it never returns prompt or transcript content through its API.
- Aggregated history is cached locally in `~/.codex-meter/cache-v2.json`.
- The web UI has a restrictive Content Security Policy and no third-party scripts, fonts, analytics, or network calls.
- The Windows project store contains project paths, task/thread IDs, display titles, worktree/checkpoint object IDs, routing locks, execution profiles, and UI preferences only. Generated adaptive-agent configs contain only role instructions plus model and effort IDs. Codex remains the transcript source of truth.
- Optional **Encrypted continuity** stores queue payloads, goals, and handoff state under the current Windows user with DPAPI. It is disabled by default. Scheduled instructions, monitor classifications, run summaries, and bounded Work Receipt evidence are always DPAPI-encrypted because persistence is required for scheduling and verification history.

Codex Meter has no authentication. Do not bind it to `0.0.0.0` or expose it to the internet without putting an authenticated reverse proxy in front of it.

## What “exact” means

Token totals are the exact values recorded in local Codex session telemetry. Current quota percentages and reset timestamps are also the values reported by Codex.

Codex does not expose a universal absolute “weekly token allowance” in these events. Quota consumption can include model, reasoning, or service-tier weighting, so the dashboard does not invent an absolute cap or pretend raw token totals map one-to-one to plan quota.

This dashboard tracks Codex sessions stored on the current machine. It does not include sessions created elsewhere unless those session files are present locally, and it does not track OpenAI API Platform billing.

## Development

```bash
npm run dev
npm test
npm run check
```

The project uses only Node.js built-ins and browser-native HTML, CSS, and JavaScript.

For a complete clone-to-release setup, Windows debug commands, test matrix, local data map, and release checklist, read the [development guide](DEVELOPMENT.md). For repository structure and data boundaries, see [Architecture](docs/ARCHITECTURE.md); for concrete feature ownership, see [Extending the system](docs/EXTENDING.md). Contributions are welcome through [CONTRIBUTING.md](CONTRIBUTING.md).

## Official Codex references

- [Codex CLI slash commands](https://developers.openai.com/codex/cli/slash-commands)
- [Codex pricing and usage limits](https://developers.openai.com/codex/pricing)
- [Codex token-based rate card](https://help.openai.com/en/articles/20001106)
- [OpenAI API model prices](https://developers.openai.com/api/docs/models/compare)
- [Codex documentation](https://developers.openai.com/codex)

Codex Meter is an independent, unofficial source-available project and is not affiliated with or endorsed by OpenAI.

## License

[PolyForm Noncommercial 1.0.0](LICENSE) © 2026 mckrosekri. Commercial use requires a separate written license. See [LICENSING.md](LICENSING.md) and the [Contributor License Agreement](CONTRIBUTOR_LICENSE_AGREEMENT.md).
