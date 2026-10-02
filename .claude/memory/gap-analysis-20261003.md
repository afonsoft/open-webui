# gap-analysis-20261003 — audit pós-merge Epic #48

## Estado da run

- Branch: main @ 8b01cd081 | build Release 0/0 | testes 419 API + 7 client verdes
- Fontes: `collect-sources.sh` ok; gh auth ok; `.specs/` 5 SPECs não arquivados; docs/specs/ 23 arquivados
- Issues: Epic #48 + slices #49–#63 ainda OPEN com label `in_pullrequest` (todos merged)

## Veredictos

### CONFIRMADO → SPECs Draft gerados
| Gap key | SPEC |
|---|---|
| GAP-implementation-chats-versions-events | SPEC-20261003-chats-advanced |
| GAP-implementation-knowledge-v2 | SPEC-20261003-knowledge-v2 |
| GAP-implementation-ollama-management | SPEC-20261003-ollama-management |
| GAP-implementation-audio-engines | SPEC-20261003-audio-engines |
| GAP-implementation-retrieval-engines | SPEC-20261003-retrieval-v2 |
| GAP-implementation-notes-collab | SPEC-20261003-notes-collab |
| GAP-implementation-users-groups-granular | SPEC-20261003-permissions-granular |
| GAP-implementation-model-filters | SPEC-20261003-model-filters |
| GAP-implementation-i18n-locales | SPEC-20261003-i18n-locales |
| GAP-implementation-utils-router + community | SPEC-20261003-utils-community |
| GAP-security-rate-limiting | SPEC-20261003-rate-limiting |

### CONFIRMADO (housekeeping — sem SPEC, wrap-up do Epic #48)
- GAP-operation-wrapup-epic-11: 5 SPECs `Approved` em `.specs/` (access-grants-calendars, admin-configs-v2, frontend-routes-parity, multi-instance, users-management) devem ir a `docs/specs/` com `Completed`+`Delivered`; issues #49–63 → `done`+close; Epic #48 close; `docs/MIGRACAO-DOTNET.md` resinc (linhas 🟡/⬜ obsoletas: workspace create/edit routes, notes routes, /watch, channels access-grants, calendar multi, configs banners, evaluations export, multi-instância — todos entregues).

### REJEITADO (out-of-scope documentado nos SPECs fase-2)
- Jupyter spawn/PTY local (terminals — decisão de segurança)
- Execução de código arbitrário em functions/pipelines (plugin-ecosystem — decisão .NET)
- MySQL/MSSQL, GCS/Azure Blob, Redis session cache, K8s (multi-instance out-of-scope)
- Evaluations export — JÁ EXISTE (`/feedbacks/all/export`, EvaluationEndpoints.cs:22) — doc stale

### INCONCLUSIVO — nenhum

## Fase
5 — aguardando aprovação do gate (SPECs Draft em `.specs/SPEC-20261003-*.md`)
