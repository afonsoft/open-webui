# API

Base: `/api/v1` — Minimal APIs em `src/OpenWebUI.Api/Endpoints/`. Auth: `Authorization: Bearer <jwt>` ou `Bearer sk-*`.

| Grupo | Arquivo | Escopo |
|---|---|---|
| `AuthEndpoints` | registro, login, perfil | usuários |
| `ChatEndpoints` | CRUD de chats/mensagens, completions (SSE), share, fixar, pastas, arquivos | chats |
| `UserEndpoints` | perfil, senha, chaves `sk-*`, admin | usuários |
| `WorkspaceEndpoints` | folders, prompts, memories, notes, models custom, files | workspace |
| `ModelEndpoints` | listagem e modelos custom | modelos |
| `EvaluationEndpoints` | feedback 👍/👎 de mensagens | avaliações |
| `TaskEndpoints` | título, follow-ups, tags via LLM | tarefas |
| `ApiEndpoints` | `/api/config` (feature flags), `/health` | sistema |

## Convenções

- Erros de provider → status de erro (`404`/`502`), nunca payload enlatado.
- Seed de conexões por env na primeira inicialização.
- `DatabaseMigrator` aplica EF Core Migrations no startup; bases legadas (sem `__EFMigrationsHistory`) são baselinadas sem recriar tabelas.
