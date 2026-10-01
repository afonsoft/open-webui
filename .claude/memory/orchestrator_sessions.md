# orchestrator_sessions

## Session — 2026-10-01 21:20

**Scope**: Bootstrap do harness + instalação do catálogo afonsoft/skills no repo afonsoft/open-webui (.NET 10 / Blazor WASM).
**Decisions**: catálogo instalado versionado em `.devin/skills/` (repo-level, não user-level); harness em `.claude/` compartilhado Claude Code + Devin CLI via `read_config_from`; sem `AGENTS.md` (Devin lê `CLAUDE.md` nativamente).
**Delivered**: PR #10 (catálogo 24 skills), PR #11 (CLAUDE.md, .claude/ completo, .devin/config.json, docs/, .specs/, orchestrator_stats.md) — ambos mergeados.
**Remaining**: nenhuma fila — 0 issues abertas em afonsoft/open-webui, nenhum SPEC; pendências de paridade documentadas em docs/MIGRACAO-DOTNET.md (RAG, OAuth/LDAP, voice, realtime, i18n...) aguardam decisão do usuário para virar SPECs/Issues.
**Lessons**: `gh` dentro do clone resolve para `open-webui/open-webui` (remote `upstream` vence) — usar sempre `--repo afonsoft/open-webui`.
