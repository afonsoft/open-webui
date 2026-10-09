# gap-analysis run — 2026-10-09 — Web IDE (opencode → open-webui)

Resume state for the opencode→open-webui Web-IDE gap analysis. Full report: `docs/pt/ANALISE-IDE-WEB-OPENCODE.md` + `docs/en/ANALYSIS-WEB-IDE-OPENCODE.md`.

## Sources audited

- `.specs/` (3 open), `docs/specs/` (chat-agent-* delivered series — prior partial opencode parity), `docs/architecture/`, `CLAUDE.md`, `.claude/rules/`, `README.md`
- AS-IS: `src/OpenWebUI.Infrastructure/ChatTools/` (14 builtin tools), `WorkspaceRepoService`, `WorkspaceGitService`, `TerminalPtyEndpoints`, `ChatRun*`, `Client/Pages/Admin.razor`, `ChatWorkspacePanel.razor`, `ChatView.razor`, `Workspace.razor`
- TO-BE reference: `afonsoft/opencode` dev @ ecc4916 — `packages/opencode/src/{tool,session,skill,command,snapshot,permission,lsp,worktree,agent}/`, `packages/app`
- User asks (verbatim): redo the opencode analysis, migrate coding+testing features → Web IDE; "ler as skill do repositório e criar o slash command da skill"

## Verdicts

- CONFIRMADO (actionable): `GAP-impl-ide-surface`, `GAP-impl-workspace-file-api`, `GAP-impl-repo-skills`, `GAP-impl-project-instructions`, `GAP-impl-agent-modes`, `GAP-impl-checkpoints`, `GAP-impl-at-mentions`, `GAP-impl-test-runner`, `GAP-impl-lsp`, `GAP-impl-worktree`, `GAP-impl-apply-patch`
- DUPLICADO-parcial: `GAP-arch-permission-rulesets` (fold into modes spec)
- REJEITADO: `GAP-impl-code-mode`, `GAP-impl-share-sync`, `GAP-impl-tui-acp`
- `[A DEFINIR]` (architecture decision, not a spec gap): `GAP-sec-sandbox` (container-per-workspace vs host+approvals)

## Draft SPECs written (gate pending)

| SPEC | Slice | Priority |
| --- | --- | --- |
| `.specs/SPEC-20261009-workspace-file-api.md` | S1 file REST API | high |
| `.specs/SPEC-20261009-web-ide-surface.md` | S2 /ide page (CM6, tree, terminal, changes) | high |
| `.specs/SPEC-20261009-repo-skills-slash-commands.md` | S3+S4 skills→slash cmds + AGENTS.md injection | high (explicit ask) |
| `.specs/SPEC-20261009-agent-modes-plan-build.md` | S5 plan/build + rulesets + always | medium |
| `.specs/SPEC-20261009-checkpoints-revert.md` | S6 per-turn git snapshot + revert | medium |
| `.specs/SPEC-20261009-ide-mentions-tests.md` | S7 @file mentions + run-tests UX | medium |
| `.specs/SPEC-20261009-lsp-diagnostics.md` | S8 LSP | low (phase 2) |
| `.specs/SPEC-20261009-worktree-format-hooks.md` | S9 worktree/apply_patch/format | low |

Dependency order: S1 → S2 → (S3+S4 ∥ S5) → S6 → S7 → S8/S9.

## Gate

Awaiting user approval ("sim/não") before: GitHub Issues (epic + slices per `create-issues`), branch/commit/push, orchestrator execution. No external action taken.
