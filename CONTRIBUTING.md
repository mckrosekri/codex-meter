# Contributing

Thank you for improving Codex Meter and Codex Decision. Contributions of code, tests, documentation, design feedback, and reproducible bug reports are welcome.

## Before you start

1. Read the [development guide](DEVELOPMENT.md) and [architecture](docs/ARCHITECTURE.md).
2. Check existing issues and pull requests before starting a large change.
3. Open a focused issue for behavior that changes storage, networking, permissions, App Server protocol, Git safety, or product direction.
4. Never share raw `.codex` sessions, prompts, credentials, account data, or unredacted personal paths.
5. Read the [Contributor License Agreement](CONTRIBUTOR_LICENSE_AGREEMENT.md); copyrightable contributions require explicit acceptance in the pull request.

Small fixes and documentation improvements can go directly to a pull request.

## Development workflow

Create a short branch from the current target branch:

```powershell
git switch -c fix/short-description
```

Keep commits reviewable and use an imperative subject. Conventional prefixes are encouraged:

```text
feat: add receipt history filters
fix: keep sidebar collapse state stable
docs: clarify Windows source setup
test: cover App Server reconnect cancellation
```

Follow [Extending the system](docs/EXTENDING.md) to place behavior in the correct core, UI, persistence, or integration boundary. Do not mix unrelated cleanup into a feature or fix.

## Required verification

Run the web gate for every pull request:

```powershell
npm.cmd test
npm.cmd run check
```

Run the Windows core suite when changing .NET code, Windows documentation, persistence contracts, or shared usage rules:

```powershell
dotnet test .\windows\CodexDecision.Core.Tests\CodexDecision.Core.Tests.csproj --configuration Debug -p:Platform=x64
```

Build the WinUI application when changing desktop code or packaging:

```powershell
dotnet build .\windows\CodexMeterTray\CodexMeterTray.csproj --configuration Debug -p:Platform=x64
```

User-facing changes also need a manual UI pass. State exactly what was clicked and observed in the pull request. Include a screenshot or trace when practical, but do not commit private data or temporary QA output.

## Project invariants

- Codex remains the transcript source of truth. Do not copy prompts or assistant messages into the workspace store, usage cache, diagnostics, receipts, or analytics.
- Keep the web server bound to loopback by default and do not add remote telemetry or third-party browser resources.
- Automatic permissions are limited to read-only and workspace-write. Full access always requires explicit user selection and confirmation.
- Never silently replace an unavailable explicitly locked model or reasoning effort.
- Preserve routing precedence: one-turn choice, task lock, project lock, then Full Auto.
- Validate Git and file targets against the intended repository or managed-worktree root.
- Keep App Server requests typed and do not replay requests whose acceptance is uncertain after a transport failure.
- Encrypt prompt-bearing continuity, automation, and verification state with Windows DPAPI for the current user.
- Label rate limits, raw tokens, weighted usage, and API-equivalent cost estimates honestly.
- Avoid new web dependencies unless they materially reduce risk or maintenance.

## Tests and fixtures

Add or update focused tests for routing, permissions, protocol parsing, workspace migration, Git safety, integration management, token aggregation, billing cycles, and pricing logic. Unit tests must be deterministic and must not depend on the contributor's Codex account.

Real App Server and local telemetry checks remain explicitly opt-in through the environment variables documented in [DEVELOPMENT.md](DEVELOPMENT.md). Convert any discovered regression into a synthetic test before submitting it.

## Pull requests

A good pull request:

- Explains the user-visible problem and the chosen boundary for the fix.
- Calls out privacy, permission, persistence, Git, or migration effects.
- Includes automated tests or explains why the change is documentation-only.
- Lists manual UI steps and results for user-facing work.
- Contains no generated `artifacts/`, `bin/`, `obj/`, logs, local state, session data, or incidental QA evidence.
- Keeps release/version changes separate unless the pull request is intentionally preparing a release.

All CI jobs must pass. A maintainer may ask for a smaller change or an additional regression test before merge.

## AI-assisted contributions

AI assistance is welcome, but the contributor remains responsible for understanding the change, reviewing every diff, testing it, and removing private or generated material. Mention substantial AI assistance in the pull-request notes when it helps reviewers understand how the change was produced.

## License

The project is source-available under the [PolyForm Noncommercial License 1.0.0](LICENSE), with commercial licensing reserved to `mckrosekri`. Copyrightable contributions are accepted only under the [Contributor License Agreement](CONTRIBUTOR_LICENSE_AGREEMENT.md), which lets the owner distribute and relicense them under noncommercial or commercial terms while contributors retain ownership. Third-party code or assets must have compatible terms and retain all required attribution and notices. See [LICENSING.md](LICENSING.md) for the full policy.
