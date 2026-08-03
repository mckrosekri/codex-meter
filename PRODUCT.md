# Product

## Product purpose

Give Windows Codex users the project/task/chat experience they already understand, while automatically choosing an appropriate model, reasoning effort, and safe permission level for each turn. Preserve the repository's exact local Usage dashboard as a secondary surface.

## Users

Codex users who work across multiple local codebases and want sensible routing without repeatedly choosing a model or permission mode. Expert users must still be able to override one turn or lock a choice for a task or project.

## Product contract

- Codex App Server remains the execution and transcript source of truth.
- Full Auto makes an explainable route from the models and efforts Codex reports as available.
- Adaptive Full Auto may delegate bounded phases to native model-specialized Codex agents; One-model Full Auto keeps the initial automatic route for the whole turn.
- Precedence is one turn, then task lock, then project lock, then Full Auto.
- Auto permissions stop at workspace-write. Full access is manual and explicitly confirmed.
- Locked models and efforts never silently fall back.
- Explicit model or effort choices and locks suppress adaptive handoffs; permissions and special skills remain inherited across workers.
- Project metadata stays local and contains no prompt or transcript text.
- Special skills are explicit, visible, locally persisted by ID, and attached without replacing the user's visible message.
- Usage remains available from the app navigation and system tray.

## Brand personality

Calm, precise, and candid. The client should feel like a focused coding workspace, not an analytics-heavy SaaS dashboard.

## Design principles

1. Keep chat and the current task primary.
2. Put model, lock, effort, and permission controls directly in the composer.
3. Explain automatic routing without interrupting normal work.
4. Make approvals and elevated permissions unmistakable.
5. Keep plans, commands, tools, and diffs visible but secondary.
6. Use adaptive navigation so the composer remains usable at narrow desktop widths.
7. Keep optional specialization controls collapsed in the left panel until the user needs them.

## Accessibility

Use strong contrast, visible keyboard focus, semantic WinUI controls, accessible names, color-independent status text, and responsive layouts. Ctrl+Enter sends; Enter inserts a line break.
