# Wrap-up — Epic #79 (gap-analysis-20261003, fase 3)

Data: 2026-10-02

## Entrega

11/11 slices implementadas e mergeadas:

| Issue | Slice | PR |
|---|---|---|
| #90 | rate-limiting | #93 |
| #80 | chats-advanced | #94 |
| #81 | knowledge-v2 | #95 |
| #86 | permissions-granular | #96 |
| #87 | model-filters | #97 |
| #88 | i18n-locales | #98 |
| #89 | utils-community | #99 |
| #84 | retrieval-v2 | #100 |
| #82 | ollama-management | #103 |
| #83 | audio-engines | #104 |
| #85 | notes-collab | #105 |

Fixes auxiliares: #101/#102 (rerank determinístico — flake de ordem de
leitura do SQLite) e bump monotônico do `UpdatedAt` em `NoteUpdate`
(carregado pelos merges de #95/#97 — flake revelado pelo CI).

## Estado final

- 487 testes verdes (477 API + 10 client), 0 warnings
- SPECs das 11 slices arquivados em `docs/specs/` como `Completed`
- Issues #80–#90 fechadas (`done`), Epic #79 fechado
- Paridade resincada em `docs/MIGRACAO-DOTNET.md`: ~31 ✅ / ~2 🟡
  (decisões documentadas) / 0 ⬜

## Pendências residuais (decisões documentadas, não bugs)

- Tools/functions em código arbitrário (upstream roda Python do usuário)
  — decisão plugin-ecosystem: filters declarativos somente.
- Spawn de Jupyter local / PTY no host — decisão: só proxy externo.
- Rotas dedicadas de edição de functions (hoje aba admin inline).

## Lições da fase

- SignalR testável sem sockets: `HttpTransportType.LongPolling` +
  `factory.Server.CreateHandler()` + `AccessTokenProvider`.
- LWW por `UpdatedAt` (epoch-s): writes no mesmo segundo colidem —
  `Math.Max(now, prev+1)` garante versão distinta.
- Signups de teste após o primeiro precisam de `DefaultUserRole="user"`
  antes de nascerem (pending → token vazio → 401 no /ws).
- `git rerere` pode reverter código em merges repetidos — sempre
  `git -c rerere.enabled=false merge` + diff vs main deve listar só
  os arquivos da slice.
