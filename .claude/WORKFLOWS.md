# WORKFLOWS.md — Automação

## CI (`.github/workflows/dotnet.yml`)

| Gatilho | Etapas | Critério de sucesso |
|---|---|---|
| push/PR em `main` tocando `src/`, `tests/` | restore → build → test | build 0 erros, 21/21 testes NUnit |

Workflows são protegidos — modificação exige revisão humana.

## Loop de verificação

`Saída do agente → build → test → CI → revisão → merge`

## Workflow de feature

1. Branch `feature/{Agent}-{YYYYMMDD}-{slug}` a partir de `main`.
2. Implementar + testes proporcionais.
3. `dotnet build` + `dotnet test` local.
4. PR para `main`; CI verde → merge pelo usuário.

## Workflow de layout (paridade upstream)

1. Obter componente Svelte de referência (`git show FETCH_HEAD:...`).
2. Portar classes para o `.razor` equivalente.
3. Regenerar `wwwroot/css/tailwind.css` e commitar.
4. Validar visual (screenshot) antes do PR.

## Workflow de E2E

- App via Docker (`docker compose up`) ou `dotnet run` (:8080).
- Provider mockável: Ollama fake servindo `/api/tags` + `/api/chat` (NDJSON); seed via `OLLAMA_BASE_URL`.
- Skill: `.devin/skills`/`openwebui-dotnet-e2e` (quando instalada).

## Rollback

- Reverter merge via `git revert` em branch + PR; nunca force-push em `main`.
- SQLite: `SchemaBootstrap` evolui sem downgrade — backup de `webui.db` antes de mudança de schema.
