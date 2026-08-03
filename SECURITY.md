# Security policy

## Supported versions

Security fixes are applied to the latest published release and the active development branch. Older releases may not receive backports; users should update to the newest release after a fix is published.

## Report a vulnerability privately

Use GitHub's private [Report a vulnerability](https://github.com/mckrosekri/codex-meter/security/advisories/new) flow. Do not open a public issue when a report could expose local files, prompts, telemetry, credentials, command execution, Git data, or another user's machine.

Include:

- The affected version or commit.
- Operating system and relevant Codex CLI version.
- The smallest reproducible sequence.
- Expected impact and any known workaround.
- A redacted proof of concept when one is needed.

Never attach raw Codex session files, prompts, access tokens, cookies, API keys, account identifiers, or an unredacted `%LOCALAPPDATA%\CodexDecision` directory. Maintainers will coordinate disclosure and credit with the reporter before publishing details.

## Security boundaries

Codex Meter and Codex Decision are local software:

- The web server binds to `127.0.0.1` by default and has no authentication.
- The browser UI uses the local server and includes no analytics SDK or remote application database.
- The Windows client starts the installed Codex App Server locally and relies on its existing authentication state.
- Codex remains the source of truth for transcripts; the workspace and usage stores contain metadata or aggregates, not messages.
- Prompt-bearing continuity, scheduled definitions, summaries, and Work Receipt evidence are protected with Windows DPAPI for the current user.
- App-owned process trees are guarded, Git/file targets are scoped to validated roots, and verification output is bounded.
- Automatic permissions are limited to read-only and workspace-write with network access disabled. Full access requires explicit confirmation.
- Explicit model and effort locks fail visibly when unavailable rather than silently falling back.
- Bundled special skills are static packaged files. Enabling one stores its stable ID; the app does not download executable skill code at runtime.

Changing the bind address, exposing the web dashboard through a public URL, weakening sandbox policy, or replacing local storage with a shared service is outside the default security model and requires a separate threat review plus authentication and authorization design.

## Security-sensitive changes

Changes involving storage, exports, network access, child processes, shell/WSL execution, App Server messages, Git operations, encryption, updates, installers, skills/plugins/MCP, or permission selection should include:

- A clear trust-boundary and failure-mode explanation.
- Tests for invalid, stale, canceled, and unauthorized inputs.
- Redaction and retention behavior for any new data.
- Manual verification using synthetic data.
- Documentation updates in [DEVELOPMENT.md](DEVELOPMENT.md) and [Architecture](docs/ARCHITECTURE.md).

See [Contributing](CONTRIBUTING.md) for the required project invariants.
