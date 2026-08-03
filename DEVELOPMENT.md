# Development guide

This is the source-of-truth guide for setting up Codex Meter and Codex Decision, running them locally, making changes, testing those changes, and preparing a release.

## Repository at a glance

The repository contains two applications that share product rules but use different runtimes:

| Area | Purpose | Main entry point |
| --- | --- | --- |
| `src/`, `public/`, `test/` | Dependency-free Node.js usage dashboard | `src/server.js` |
| `windows/CodexDecision.Core/` | Testable routing, App Server, project, Git, conversation, integration, and usage logic | Domain-specific classes |
| `windows/CodexMeterTray/` | WinUI 3 desktop shell, pages, tray process, and service composition | `App.xaml.cs` |
| `windows/CodexDecision.Core.Tests/` | xUnit tests for the Windows core | `*Tests.cs` |
| `scripts/`, `installer/` | Self-contained Windows publish and Inno Setup packaging | `scripts/build-windows.ps1` |
| `.github/workflows/` | Linux/Windows CI and tag-driven releases | `ci.yml`, `release.yml` |

Read [Architecture](docs/ARCHITECTURE.md) for the runtime boundaries and [Extending the system](docs/EXTENDING.md) before adding a feature.

## Prerequisites

Install the tools needed by the part of the repository you plan to change.

### All contributors

- Git
- Node.js 20.11 or newer; CI currently uses Node.js 22
- PowerShell 7 or Windows PowerShell for the documented Windows commands

The web application uses only Node.js built-ins, so there are no npm dependencies to install.

### Windows desktop contributors

- Windows 10 version 1809 or newer, or Windows 11
- .NET 10 SDK
- An installed and authenticated Codex CLI available as `codex` or `codex.cmd`
- Optional: Visual Studio with .NET desktop development and Windows App SDK support
- Optional: Inno Setup 6, required only to build the installer
- Optional: WSL, required only to test WSL execution profiles

If Codex is installed in a nonstandard location, set `CODEX_BIN` to the executable or command path. `CODEX_CLI_PATH` is also recognized by the integration-management service. `CODEX_HOME` can point both applications at a nondefault Codex data directory.

Check the toolchain before debugging an application problem:

```powershell
git --version
node --version
dotnet --info
codex --version
```

Authenticate the Codex CLI using its normal login flow before running the Windows integration tests or starting a real chat.

## Clone and first verification

```powershell
git clone https://github.com/mckrosekri/codex-meter.git
Set-Location codex-meter
git status
npm.cmd test
npm.cmd run check
dotnet test .\windows\CodexDecision.Core.Tests\CodexDecision.Core.Tests.csproj --configuration Debug -p:Platform=x64
```

PowerShell may block the `npm.ps1` shim on some machines. The `npm.cmd` form above bypasses that shim without changing the machine execution policy.

## Run the web dashboard

Start the normal server:

```powershell
npm.cmd start
```

Open <http://127.0.0.1:4317>. For automatic restart while editing server files, use:

```powershell
npm.cmd run dev
```

Configuration:

| Variable | Default | Meaning |
| --- | --- | --- |
| `CODEX_HOME` | `%USERPROFILE%\.codex` | Codex telemetry root |
| `HOST` | `127.0.0.1` | HTTP bind address |
| `PORT` | `4317` | HTTP port |

The server has no authentication. Keep `HOST` on loopback during development unless an authenticated local proxy is part of the test.

## Build and run the Windows app

The command-line path most closely matches CI:

```powershell
dotnet restore .\windows\CodexMeterTray\CodexMeterTray.slnx
dotnet build .\windows\CodexMeterTray\CodexMeterTray.csproj --configuration Debug -p:Platform=x64
& .\windows\CodexMeterTray\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\CodexMeterTray.exe --show
```

The app is single-instance. Starting it again with `--show` activates the existing process. To close that process from a build output, run the same executable with `--exit`.

In Visual Studio, open `windows/CodexMeterTray/CodexMeterTray.slnx`, select `x64`, make `CodexMeterTray` the startup project, and run the unpackaged application.

### Build a self-contained app or installer

Publish without creating an installer:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-windows.ps1 -SkipInstaller
```

The self-contained output is written to `artifacts\windows\win-x64`.

To build the installer, install Inno Setup 6 and omit `-SkipInstaller`:

```powershell
winget install --id JRSoftware.InnoSetup -e
powershell -ExecutionPolicy Bypass -File .\scripts\build-windows.ps1
```

The installer is written to `artifacts\installer\CodexDecisionSetup-<version>.exe`. `artifacts/` is generated and must not be committed.

## Test strategy

Run the smallest relevant tests while iterating, then the complete local gate before opening a pull request.

### Web

```powershell
npm.cmd test
npm.cmd run check
```

The tests cover usage-store aggregation, subscription cycles, and pricing. The check script parses every server and browser JavaScript entry point.

### Windows core

```powershell
dotnet test .\windows\CodexDecision.Core.Tests\CodexDecision.Core.Tests.csproj --configuration Debug -p:Platform=x64
```

The ordinary suite is deterministic and does not require a real Codex process or private telemetry.

### Opt-in local integrations

These tests intentionally use the current developer's Codex installation or local telemetry and are disabled by default:

```powershell
$env:RUN_CODEX_INTEGRATION = '1'
dotnet test .\windows\CodexDecision.Core.Tests\CodexDecision.Core.Tests.csproj --configuration Debug -p:Platform=x64 --filter RealAppServer
Remove-Item Env:RUN_CODEX_INTEGRATION

$env:RUN_USAGE_INTEGRATION = '1'
dotnet test .\windows\CodexDecision.Core.Tests\CodexDecision.Core.Tests.csproj --configuration Debug -p:Platform=x64 --filter RealLocalTelemetryCanBeIndexedWhenIntegrationIsEnabled
Remove-Item Env:RUN_USAGE_INTEGRATION
```

Never commit local session data or attach it to a bug report. Convert a failure into a minimal synthetic fixture with prompts, account details, and personal paths removed.

### UI verification

Changes to WinUI pages or browser behavior require a manual pass in addition to automated tests. At minimum, verify:

1. Fresh start and second-instance activation.
2. Sidebar expand/collapse, project expand/collapse, task selection, and navigation back/forward.
3. New question-only task and optional project task creation.
4. Composer send, queue/steer behavior, stop, approvals, and completion state.
5. Settings and Skills/Plugins/MCP add, edit, enable/disable, remove, and refresh flows.
6. Usage refresh and local web layout at desktop and narrow widths.
7. Tray minimize/restore and clean exit.

Record the exact path tested and preserve a screenshot or trace for user-facing changes when practical. Do not commit QA recordings unless a maintainer requests them.

## Local data and diagnostics

Codex transcripts remain in the Codex home directory. The desktop application stores its own metadata below `%LOCALAPPDATA%\CodexDecision`; aggregate web cache data is stored in `%USERPROFILE%\.codex-meter`.

Important desktop paths include:

| Path below `%LOCALAPPDATA%\CodexDecision` | Contents |
| --- | --- |
| `workspace-v2.json` | Project/task metadata and preferences, not transcripts |
| `usage-cache-v2.json` | Aggregate token and scan data |
| `usage-preferences-v1.json` | Renewal day and pricing preference |
| `secure-queue-v1.json` | Optional DPAPI-encrypted continuity |
| `automations-v1.json` | DPAPI-encrypted scheduled definitions and summaries |
| `work-receipts.json` | DPAPI-encrypted bounded verification evidence |
| `adaptive-agents/` | Generated model/effort role configuration |
| `worktrees/` | App-managed Git worktrees |
| `logs/app-errors.log` | Crash and unhandled-exception diagnostics |

Exit the app before backing up or moving its state. Never manually delete an app-managed worktree; use the app's confirmed cleanup flow so Git metadata is removed consistently.

## Debugging sequence

For a startup, navigation, or App Server failure:

1. Reproduce with a Debug build and note the exact click sequence.
2. Inspect `%LOCALAPPDATA%\CodexDecision\logs\app-errors.log`.
3. Run `codex --version` and confirm `codex app-server --stdio` can start from the same shell.
4. Check whether `CODEX_BIN`, `CODEX_CLI_PATH`, or `CODEX_HOME` overrides are active.
5. Add a focused regression test in `CodexDecision.Core.Tests` when the failure can be isolated from WinUI.
6. Re-run the UI sequence, including collapsing and restoring navigation around the changed page.

For usage mismatches, compare only synthetic `token_count`, `turn_context`, and `session_meta` events. The intended accounting model is documented in [Architecture](docs/ARCHITECTURE.md) and [Pricing](docs/PRICING.md).

## Release process

Maintainers prepare a release on a normal branch and use a `v<major>.<minor>.<patch>` tag only after CI and manual UI QA pass.

1. Update the version in `package.json`, `windows/CodexMeterTray/CodexMeterTray.csproj`, `windows/CodexMeterTray/Package.appxmanifest`, `scripts/build-windows.ps1`, and `installer/CodexMeter.iss`.
2. Update versioned output examples and release notes where needed.
3. Run the full web and Windows test gates.
4. Build the self-contained output and installer locally.
5. Install the generated package, verify launch, task navigation, tray behavior, integrations, and uninstall.
6. Commit and push the release preparation.
7. Create and push the matching tag, for example `v0.4.1`.

The tag triggers `.github/workflows/release.yml`, which builds the installer, computes a SHA-256 file, and creates or updates the GitHub release. Do not reuse a version number for different source.

## Continue building

- Start with [Extending the system](docs/EXTENDING.md) to find the correct ownership boundary.
- Follow [Contributing](CONTRIBUTING.md) for branch, commit, and pull-request expectations.
- Keep [Security policy](SECURITY.md) and the privacy boundary in mind before adding storage, networking, commands, permissions, exports, or integrations.
- Use [Roadmap](ROADMAP.md) for product direction, but keep each pull request independently testable and reviewable.

Before submitting copyrightable code or substantial documentation, read and accept the [Contributor License Agreement](CONTRIBUTOR_LICENSE_AGREEMENT.md). The public project is source-available for noncommercial use; see [Licensing](LICENSING.md) for commercial and historical terms.
