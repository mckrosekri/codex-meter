# Codex Decision for Windows

Codex Decision is a local-first WinUI 3 client built on the installed Codex App Server. It keeps the familiar project/task/chat shape while adding a decision layer beside the model selector.

## What it provides

- Question-only chats that do not require Git, plus optional local projects with their existing Codex tasks synchronized by working directory.
- Streaming user and Codex messages, plans, reasoning summaries, tools, commands, file changes, approvals, and turn diffs.
- Adaptive and one-model Full Auto routing for model, reasoning effort, and safe permissions.
- Manual one-turn selectors plus task-level and project-level locks.
- Permission choices for Auto safe, read-only, workspace-write, and manually confirmed full access.
- Native active-turn steering plus a visible multi-message follow-up queue with edit, reorder, send-now, and delete controls.
- A collapsible Special skills panel with an opt-in Prompt Master specialization for OpenAI Codex.
- The exact local Usage dashboard in the navigation footer, including every active rate-limit window, historical token totals, billing cycles, model-aware pricing, and tray refresh behavior.
- Supervised App Server recovery, task health/context badges, approval/completion notifications, encrypted continuity, and Goal mode.
- Managed Git worktrees, safe Local handoff/cleanup, Git review actions, and previewable tracked-file turn undo/redo.
- Encrypted Work Receipts and opt-in project Verification Gates with queue and Git protection.
- Conversation branching, editable fresh handoffs, local file/image attachments, Markdown/JSON export, title search, and task pin/archive/rename.
- Native Windows/WSL execution profiles, encrypted scheduled monitors and follow-ups, a recent-run attention inbox, and native Codex skills/plugins/MCP inventory.

Full Auto never chooses full access. Network access is disabled in automatic read-only and workspace-write sandbox policies. A locked model is never silently replaced.

Choose **Full Auto / Adaptive** to let the initial automatic route coordinate a long task while native Codex subagents handle useful bounded phases: efficient exploration, well-scoped implementation, deterministic verification, and high-judgment review. Choose **Full Auto / One model** to keep the entire turn on its initial automatic model. Adaptive mode avoids delegation for narrow tasks, does not switch models around individual commands, and is suspended by an explicit model or effort choice or lock. Permissions are inherited from the parent turn, enabled special skills remain attached, and actual handoffs appear in the Activity pane. There is no separate routing-model preflight call; each spawned agent does consume its own Codex usage when delegated.

Choose **Queue while working** or **Steer while working** beside the routing controls. Ctrl+Enter uses the selected behavior; Ctrl+Shift+Enter uses the opposite behavior for one message. Queued messages start automatically after a successfully completed turn. Stopping or failing a turn pauses the remaining queue until **Run next** is selected.

The right-side **Work receipt** card records route, permission, timing, context-at-completion, and Git evidence after each completed turn. Open its settings to enable a project Verification Gate and enter one explicit command per line. Commands run in the project's configured Native Windows or WSL environment. A failed gate can pause the follow-up queue and, when Git protection is enabled, a stale or failing receipt must be rerun before commit or push. The receipt dialog exposes captured command output and exports evidence-only Markdown without conversation text.

Expand **Special skills** at the bottom of the left panel and enable **Prompt Master** to have informal requests interpreted as principal-engineering briefs. The app sends the original visible text plus a separate native App Server `skill` input, so the specialization does not pollute the chat message. The current toggle applies to new sends and steering; queue entries capture and display the skill selection they will use. Full Auto treats Prompt Master work as at least a standard engineering task while preserving explicit read-only/write intent and every routing lock.

The bundled specialization adapts the intent extraction, scope control, success criteria, tool specialization, and token-efficiency concepts from [nidhinjs/prompt-master](https://github.com/nidhinjs/prompt-master), version 1.7.0, under its MIT license. The original copyright and license are included beside the bundled skill.

## Requirements

- Windows 10 version 1809 or later, or Windows 11
- An installed and authenticated Codex CLI available as `codex.cmd`
- No separate API key; Codex App Server uses the existing Codex installation and account state

Set `CODEX_BIN` when the CLI is installed at a custom path.

## Install

1. Download `CodexDecisionSetup-<version>.exe` from the release.
2. Run the per-user installer; administrator access is not required.
3. Optionally enable **Start Codex Decision when I sign in**. It is unchecked by default.
4. Launch Codex Decision from Start or the notification-area icon.

Unsigned development builds can show an unknown-publisher warning.

## Build from source

Requirements: .NET 10 SDK and Inno Setup 6 when creating the installer.

```powershell
winget install --id JRSoftware.InnoSetup -e
powershell -ExecutionPolicy Bypass -File .\scripts\build-windows.ps1
```

Outputs:

- Self-contained x64 app: `artifacts\windows\win-x64`
- Per-user installer: `artifacts\installer\CodexDecisionSetup-0.5.0.exe`

Build without the installer:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-windows.ps1 -SkipInstaller
```

Run tests and a local App Server integration check:

```powershell
dotnet test .\windows\CodexDecision.Core.Tests\CodexDecision.Core.Tests.csproj -c Debug -p:Platform=x64
$env:RUN_CODEX_INTEGRATION = '1'
dotnet test .\windows\CodexDecision.Core.Tests\CodexDecision.Core.Tests.csproj -c Debug -p:Platform=x64 --filter RealAppServer
$env:RUN_USAGE_INTEGRATION = '1'
dotnet test .\windows\CodexDecision.Core.Tests\CodexDecision.Core.Tests.csproj -c Debug -p:Platform=x64 --filter RealLocalTelemetryCanBeIndexedWhenIntegrationIsEnabled
```

## Local data and privacy

Codex stores task transcripts. Codex Decision renders them on demand and does not duplicate them in its project store. `%LOCALAPPDATA%\CodexDecision\workspace-v2.json` contains metadata and preferences only; the v1 store is retained after successful migration. Queues and goals are memory-only by default. Enabling **Encrypted continuity** writes DPAPI Current User ciphertext to `secure-queue-v1.json`. Scheduled instructions and run summaries require persistence and are DPAPI-encrypted in `automations-v1.json`. Work Receipt evidence and bounded verification output are DPAPI-encrypted in `work-receipts.json`; prompts and transcript text are never included. `%LOCALAPPDATA%\CodexDecision\adaptive-agents` contains namespaced role instructions and model/effort IDs, never prompts or transcripts.

The Usage page incrementally indexes `token_count` telemetry from local active and archived sessions using the same cumulative-delta rules as the web dashboard. `%LOCALAPPDATA%\CodexDecision\usage-cache-v2.json` stores aggregate token counts, dates, model IDs, and scan metadata only; `%LOCALAPPDATA%\CodexDecision\usage-preferences-v1.json` stores the renewal day and pricing basis. Neither file retains prompts, transcripts, credentials, or API keys. No analytics SDK is included.
