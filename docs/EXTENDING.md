# Extending the system

Use this guide to place new work in the correct layer and preserve the local-first, least-privilege design.

## Dependency direction

`CodexDecision.Core` contains behavior that can be tested without WinUI. `CodexMeterTray` composes those services and renders them. Core must not depend on the desktop project.

```text
CodexMeterTray (WinUI, tray, composition)
        |
        v
CodexDecision.Core (domain and integrations)
        |
        +-- Codex App Server child process
        +-- Git / Windows / WSL processes
        +-- local aggregate or DPAPI-encrypted state
```

The web dashboard is a separate Node.js surface. It shares accounting behavior conceptually, not through a runtime dependency, so usage and pricing changes must be tested in both implementations.

## Ownership map

| Change | Primary location | Tests or paired files |
| --- | --- | --- |
| Model, effort, or permission selection | `windows/CodexDecision.Core/Routing/` | `AutomaticDecisionRouterTests.cs`, `AdaptiveRoutingTests.cs` |
| App Server protocol, lifecycle, or recovery | `windows/CodexDecision.Core/AppServer/` | App Server connection/client/supervisor tests and `FakeAppServerTransport.cs` |
| Queue, goal, automation, receipt, or task runtime | `windows/CodexDecision.Core/Conversations/` | Matching conversation tests |
| Projects, tasks, execution profiles, or persistence | `windows/CodexDecision.Core/Projects/` | Workspace, migration, catalog, and execution tests |
| Git status, worktrees, checkpoint, commit, or push | `windows/CodexDecision.Core/Git/` | Git worktree and checkpoint tests |
| Skills, plugins, and MCP management | `windows/CodexDecision.Core/Integrations/` | `IntegrationManagementTests.cs` |
| Bundled special-skill identity | `windows/CodexDecision.Core/SpecialSkills/` | `SpecialSkillCatalogTests.cs` and packaged skill files |
| Token indexing, cycles, or pricing | `windows/CodexDecision.Core/Usage/` and web equivalents | Usage tests in both .NET and Node.js |
| Service construction and shared app lifetime | `windows/CodexMeterTray/Services/AppServices.cs` | Core tests where possible |
| Navigation and project/task sidebar | `windows/CodexMeterTray/Views/ShellPage.*` | Manual navigation regression pass |
| Conversation UI | `windows/CodexMeterTray/Views/ChatPage.*` | Core tests plus manual stream/queue/approval pass |
| Integrations UI | `windows/CodexMeterTray/Views/IntegrationsPage.*` | Integration tests plus full CRUD UI pass |
| Usage UI | `windows/CodexMeterTray/Views/MainPage.*` or `public/` | Usage tests plus visual comparison |
| Tray/startup/single instance | `windows/CodexMeterTray/App.xaml.cs` | Manual first/second instance and exit pass |

## Adding domain behavior

1. Put models and behavior in the narrowest `CodexDecision.Core` namespace.
2. Keep process, file, time, or App Server boundaries injectable when deterministic testing needs a fake.
3. Add the regression or feature tests before wiring the UI.
4. Register the service in `AppServices` only when it needs application-wide lifetime.
5. Keep page code focused on view state, event translation, and rendering.

Avoid placing persistence, routing policy, Git parsing, or protocol interpretation directly in a XAML code-behind file.

## Adding an App Server capability

1. Add or extend typed records in `AppServerModels.cs`.
2. Implement request serialization and response parsing in `CodexAppServerClient.cs`.
3. Route notifications through the existing connection and runtime ownership model; do not create a second App Server process per page.
4. Cover success, malformed response, error response, cancellation, and reconnect behavior with `FakeAppServerTransport` where relevant.
5. Preserve the rule that uncertain requests are not replayed after transport failure.

Codex remains the transcript source of truth. Do not mirror conversation text into workspace metadata for convenience.

## Adding routing behavior

Routing fields resolve independently in this order: one-turn choice, task lock, project lock, then Full Auto. Preserve these invariants:

- Full Auto can select read-only or workspace-write, never full access.
- Explicit unavailable model or effort locks fail visibly; they are not silently replaced.
- A manual model or effort choice suspends adaptive delegation.
- Permission locks do not change model routing and workers inherit the parent sandbox.
- The user's visible prompt remains unchanged; native skill inputs are separate protocol items.

Update both the router tests and the Activity explanation when a new decision source is introduced.

## Adding persisted state

Treat every persisted field as a privacy and migration decision.

1. Decide whether the value belongs in metadata, aggregate usage, or prompt-bearing continuity.
2. Keep prompts, queued text, scheduled instructions, and similar content out of plaintext stores.
3. Use DPAPI Current User through the existing protector for prompt-bearing or operationally sensitive content.
4. Version the file or schema and add forward/backward migration tests.
5. Write atomically and preserve a rollback path when replacing an existing store.
6. Document the path and contents in `DEVELOPMENT.md`, `docs/ARCHITECTURE.md`, and `SECURITY.md`.

Never add telemetry upload, analytics, or a remote database as an incidental implementation detail.

## Adding a Windows page or setting

For a page, add paired `.xaml` and `.xaml.cs` files under `Views`, register navigation in `ShellPage`, and resolve shared services through `AppServices`. Keep sidebar content lightweight; interactive controls belong on pages so navigation-pane collapse does not reparent or dispose live controls.

For a setting, define its ownership first:

- UI-only and ephemeral: page state.
- Cross-page for the current process: an app service or runtime coordinator.
- Durable project/task preference: workspace models and store migration.
- Secret or prompt-bearing: DPAPI-encrypted store.

Test startup with old state, default state, changed state, and a corrupted file. Then manually test navigation-pane collapse/restore around the page.

## Adding a skill, plugin, or MCP action

User-installed integrations are managed through `CodexCliIntegrationService` and `SkillFileManager`; the GUI must support discover, add, edit, enable/disable, remove, validation, refresh, and actionable errors without requiring users to edit source code.

Bundled special skills are different: add the `SKILL.md` under `windows/CodexMeterTray/SpecialSkills`, register stable identity and metadata in `SpecialSkillCatalog`, include the file in `CodexMeterTray.csproj`, and add attribution beside the skill when its source requires it. Verify both build and publish outputs contain the file.

Do not execute downloaded code merely because an integration was discovered. Preserve explicit user control over installation and removal.

## Changing usage or pricing

The web and Windows implementations use cumulative-delta telemetry accounting and intentionally avoid prompt text. A change normally touches:

- `src/usage-store.js` and `windows/CodexDecision.Core/Usage/CodexUsageStore.cs`
- `public/cycles.js` and `windows/CodexDecision.Core/Usage/UsageCycles.cs`
- `public/pricing.js` and `windows/CodexDecision.Core/Usage/UsagePricing.cs`
- matching Node.js and .NET tests
- `docs/PRICING.md` when interpretation or rates change

Keep quota percentages distinct from raw token totals and label API-equivalent estimates as estimates, not invoices.

## Definition of done

A feature is complete when:

- Its behavior lives in the correct layer and has focused automated coverage.
- Failure, cancellation, restart, and stale-state paths are handled where applicable.
- Privacy, permission, and data-retention effects are documented.
- Web and Windows parity is checked when shared product rules change.
- User-facing UI is exercised visually, including navigation collapse/restore and narrow layouts where relevant.
- Generated artifacts, local telemetry, logs, and QA evidence are not committed.
- `npm.cmd test`, `npm.cmd run check`, and the relevant .NET test/build commands pass.
