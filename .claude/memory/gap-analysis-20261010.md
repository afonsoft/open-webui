# gap-analysis run — 2026-10-10 — pós-E16 + merges de polish

Resume state. Run on `main` @ aee318b (clean tree). Previous run: `gap-analysis-20261009.md` (E16 Web-IDE — fully delivered, PRs #236–#267 + fixes #268–#278).

## Source inventory

- PRESENT: `.specs/` (23 SPECs), `docs/` + `docs/specs/` + `docs/architecture/`, `.claude/{CONTEXT,MEMORY,rules,agents}`, `CLAUDE.md`, README; ABSENT: `AGENTS.md` (repo uses CLAUDE.md), ORCHESTRATOR-ROADMAP.md.
- AS-IS sampled: `src/OpenWebUI.{Api,Infrastructure,Client}` (LSP stack, ChatTools, WorkspaceRepoService, service-worker.js, index.html), `tools/check-*.py` guards, `.github/workflows/`, tests suite (1140 pass / 3 skip / 0 fail, Release).
- GitHub: `gh` OK (devin-ai-integration bot); no open Issues; code-scanning API → 403 on bot token (degraded — not audited).
- Sonar: MCP `sonarqube` up (`SONARQUBE_TOKEN` set); org `afonsoft` lists 27 projects — **`afonsoft_open-webui` absent** → no analyses, 0 issues.

## Verdicts

| Gap key | Verdict | Evidence |
| --- | --- | --- |
| GAP-automation-sonarcloud | CONFIRMADO | `code-quality.yml` sonarqube job gated on `SONAR_TOKEN`; project missing in SonCloud org + secret missing in repo → check is a false-green placeholder, never produced an analysis |
| GAP-tests-coverage-r2 | CONFIRMADO | measured 89.56/76.01 vs legacy 90.64/76.35; uncovered: CheckpointService ~108, PtySession ~72, LdapService ~72, ChatRunDispatcher ~60, McpClientService ~54, VideoEndpoints ~58 |
| GAP-impl-offline-init | CONFIRMADO | E2E observed: offline + cached shell → `Failed to fetch` from init-time API calls (`NotificationBell`, `Sidebar`, `Ide`) propagates → unhandled-error bar |
| GAP-sec-csp-absent | CONFIRMADO | no `Content-Security` emitters in `src/`; prod console shows report-only violations (worker-src fallback, connect-src 'none') from proxy policy |
| GAP-obs-sw-register | CONFIRMADO | `index.html:34` `navigator.serviceWorker.register(...)` fire-and-forget, no `.catch` — first-load registration failure silent (seen in E2E) |
| GAP-tests-nunit-order | CONFIRMADO | ~441 `[Order]` CS0618 warnings across Api.Tests build output |
| GAP-doc-stale-specs | CONFIRMADO | 7 SPECs in `.specs/` still `Approved` while features+CI guards shipped (a11y×4, css-classes, i18n-parity); `perf-boot`/`ui-polish` need per-SPEC verification |
| GAP-req-product-decisions | INCONCLUSIVO | D5 vault, D6 runs tree, D7 meu uso, D10 boards, D12 review bot, GAP-sec-sandbox — documented pendências awaiting user decision, not specable without it |
| i18n partial locales (6 of 8) | REJEITADO | deliberate upstream-parity subset; `check-i18n-parity` only enforces key parity of translated set |
| GAP-sec-sandbox (host exec) | DUPLICADO (held) | recorded in 20261009 run + user's pendências list — awaiting his decision |

## Draft SPECs written (gate pending)

| SPEC | Priority |
| --- | --- |
| `.specs/SPEC-20261010-sonarcloud-first-analysis.md` | high |
| `.specs/SPEC-20261010-coverage-backfill-r2.md` | medium |
| `.specs/SPEC-20261010-offline-init-resilience.md` | medium |
| `.specs/SPEC-20261010-app-csp-header.md` | medium |
| `.specs/SPEC-20261010-sw-register-observability.md` | low |
| `.specs/SPEC-20261010-nunit-orderattr-migration.md` | low |
| `.specs/SPEC-20261010-spec-status-sweep.md` | low |

## sonarqube-autofix outcome (degraded)

- Skill Phase 1 requires unresolved Sonar issues from the project — **project doesn't exist on SonarCloud** (never analyzed; job secret-gated and `SONAR_TOKEN` absent from repo secrets). Zero issues to classify → no ToDo Board, no SPECs from this skill.
- The actionable output is `SPEC-20261010-sonarcloud-first-analysis`: provision the project + secret so the real gate starts producing issues for future autofix runs.
- GitHub code-scanning alerts: API 403 on this token — not re-audited (backlog was drained to zero earlier per `.claude/MEMORY.md` history).

## Gate

Awaiting "sim/não" on: GitHub Issues (epic `gap-analysis-20261010` + slices), branch/commit/push of the draft SPECs, orchestrator handoff. No external action taken — SPECs are uncommitted drafts in `.specs/` (per repo convention, drafts stay uncommitted until approval).
