# Codex Decision v0.5.0 — protected public release

Release 5 starts the clean public product line for Codex Decision.

## Licensing

- Public source is available under PolyForm Noncommercial 1.0.0.
- Personal and other qualifying noncommercial use remains available under the license.
- Selling the software, charging for access, paid hosting, embedding it in a paid product, or other commercial use requires a separate written license from `mckrosekri`.
- The project owner retains the right to build paid editions, subscriptions, hosted services, support, and enterprise features.
- Third-party material keeps its own license and attribution.

Earlier copies received under MIT remain under their historical terms, but they are not part of this new public repository history.

## Product highlights

- Question-only tasks work without creating or selecting a Git repository.
- Optional projects support local checkouts and managed worktrees.
- Project/task/chat navigation is hardened around sidebar and project-folder collapse/expand flows.
- Skills, plugins, and MCP servers have interactive add, edit, enable/disable, remove, validation, and refresh controls.
- Adaptive routing, queue/steer controls, App Server recovery, encrypted continuity, schedules, Work Receipts, and Verification Gates remain included.
- The exact local usage meter is available in both the browser dashboard and Windows client.
- The Windows installer creates Start-menu and optional sign-in shortcuts without requiring administrator privileges.

## Build and verification

The release is built from a clean-history source tree. CI runs the Node.js tests and syntax checks, the 108-test .NET core suite, the WinUI build, and a self-contained Windows publish. The downloadable installer is accompanied by a SHA-256 checksum.

Read [DEVELOPMENT.md](../DEVELOPMENT.md) to set up the complete project and [LICENSING.md](../LICENSING.md) before using it commercially.
