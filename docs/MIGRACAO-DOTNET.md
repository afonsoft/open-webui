# Migração Open WebUI → .NET 10 / Blazor WebAssembly

Documento de acompanhamento da migração de tecnologia do Open WebUI
(SvelteKit + FastAPI/Python) para **.NET 10 · C# 14 · Blazor WebAssembly**.

> Auditoria out/2026 contra `open-webui/open-webui` upstream (~v0.11.x,
> 33 routers / ~500 endpoints, 47 rotas de página, ~250 chaves de
> `DEFAULT_CONFIG`). As 13 slices do epic `gap-analysis-20261001` foram
> entregues (PRs #29–#41); a tabela abaixo reflete o estado real **atual**,
> incluindo o que ainda falta para paridade total.

## Arquitetura alvo

```
┌─────────────────────────────────────────────┐
│ OpenWebUI.Api (ASP.NET Core 10)             │
│  ├── serve OpenWebUI.Client (Blazor WASM)   │
│  ├── /api/v1/*  (17 grupos de endpoints)    │
│  ├── /api/chat/completions  (SSE)           │
│  ├── /ws  (SignalR — canais realtime)       │
│  └── EF Core SQLite (data/openwebui.db)     │
└─────────────────────────────────────────────┘
              │                │
        Ollama /api/*    OpenAI /v1/*
```

## Resumo quantitativo (auditoria vs upstream)

| Superfície | Upstream | Coberto | Gap |
|---|---|---|---|
| Routers (grupos de endpoints) | 33 | ~20 ✅ / ~8 🟡 parcial / ~5 ⬜ | ver tabela |
| Rotas de página | 47 | ~24 | páginas de criar/editar em workspace + admin tabs |
| Engines de integração | ~15 (embeddings, busca web, imagens, TTS, extração) | ~4 | engines alternativas por família |

## Paridade por área (router upstream → .NET)

### Migrado ✅

| Área | Observação |
|---|---|
| `auths` — signup/signin/profile/api-key/admin | JWT, primeiro usuário admin, `sk-*`, aprovação de pendentes |
| `chats` — core | CRUD, busca, pin/archive, pastas, tags, share `/s/{id}`, clone, import/export, editar/regenerar mensagem |
| `users` — core | Perfil, senha, timezone, admin CRUD |
| `models` — custom | CRUD workspace, system prompt + params, toggle |
| `evaluations` — feedbacks | 👍/👎 + lista admin paginada |
| `files` — upload/serve | Extração de texto, `data/uploads/{user}/` fora do wwwroot |
| `knowledge` — RAG | Coleções, itens, embeddings (Ollama/OpenAI), retrieval por cosseno |
| `channels` — grupo | SignalR `/ws`: `message:new`, `typing`, `presence`, `@modelo` invoca provider |
| `groups` — RBAC | CRUD + membros + flags workspace/sharing/chat |
| `folders`, `memories`, `notes`, `prompts` | CRUD completo |
| `tasks` — LLM | Título, follow-ups, tags automáticas |
| `tools` — HTTP | Function calling com loop server-side (máx. 5), URL nunca exposta |
| `images` — OpenAI Images | Geração + config admin + botão no chat |
| `configs` — core | Conexões (chaves mascaradas), admin config, feature flags, export/import |
| `analytics` | Dashboard admin-only |
| `notifications` (7 eps) | Webhooks user/global, eventos (`user.pending`, `user.approved`, `automation.failed`), HMAC `X-Webhook-Signature`, `/test`, campo em Settings |
| `automations` | Agendas (interval/daily/weekly UTC) + runs + run-now + visão calendário |
| OAuth/OIDC + LDAP | Google/GitHub/Microsoft/OIDC + bind LDAP (slice auth-sso-rbac) |
| EF Migrations | `DatabaseMigrator` + baseline de `webui.db` legadas |
| Docker | Dockerfile multi-stage + compose (+ ollama opcional) |

### Parcial 🟡

| Área | Feito | Falta |
|---|---|---|
| `retrieval` (17 eps) | Embeddings + busca vetorial + `/process/{file,text,url,youtube}` + `/process/web/search` (searxng/duckduckgo/tavily/brave) + híbrido BM25 + reset db/uploads | Demais engines de busca (8), reranking por provider, engines de extração de conteúdo |
| `channels` (28 eps) | Canais em grupo + realtime + DM + threads + reações + unread + pins | Access grants por canal |
| `users` (26 eps) | Perfil/admin + user settings + sessões OAuth + busca/paginação/filtros | Permissões granulares por usuário (hoje por grupo) |
| `chats` (50 eps) | Core completo | Versões/diff de mensagens, chat-events realtime, lista admin de todos os chats |
| `knowledge` (35 eps) | RAG essencial | Anexar `file_id` a itens, access grants por item, reindex, batch ops |
| `tools` (15 eps) | Tools HTTP | Tools em código (execução server-side), valves/user settings por tool |
| `ollama`/`openai` passthrough | `/ollama/api/{tags,version,show,chat,generate,embed}` + `/openai/{models,chat/completions,embeddings}` (+ variantes indexadas) | `pull/create/delete/copy` + `blobs/*` (gerenciamento de modelos), audio/images via passthrough |
| `images` (6 eps) | Engines plugáveis: OpenAI, A1111, Gemini, ComfyUI + `/edit` + `/config/engines` + `/config/test` | Variações avançadas por engine |
| `audio` (6 eps) | `POST /audio/speech` (TTS OpenAI-compatible), `POST /transcriptions` (STT openai/deepgram), `/voices`, `/models`, `/capabilities`, fallback Web Speech | Whisper local, engines TTS extras (ElevenLabs/Azure) |
| `calendar` (13 eps) | Visão mensal + calendários reais (CRUD) + events CRUD + access grants | Busca de eventos, múltiplos calendários com cores |
| `configs` (25 eps) | Conexões/flags/admin | Banners, default models/suggestions, code-execution config, audio/image/retrieval config completa, OAuth/LDAP toggles, direct connections |
| `groups` (11 eps) | Flags workspace/sharing | Domínios allowlist, permissões granulares por feature |
| `models` (16 eps) | Custom models + arena + access grants (user/group/*) | Model filters |
| `evaluations` (15 eps) | Feedbacks + leaderboard ELO + arena battles | Export |
| `notes` (12 eps) | CRUD | Colaboração realtime (yjs), access grants |
| `i18n` | pt-BR/en-US sem reload | ~30 locales do upstream; backend não traduzido |

### Pendente ⬜

| Área | Escopo upstream |
|---|---|
| `functions` (17 eps) | Pipes/Filters/Valves — plugins de código custom do admin |
| `pipelines` (8 eps) | Framework Pipelines (inlet/outlet filters) |
| `scim` (15 eps) | Provisionamento SCIM 2.0 |
| `skills` (9 eps) | Entidade Skills do workspace (novo no upstream) |
| `terminals` (1 ep + ws) | Terminal server-side / Jupyter (proxy + WS) |
| `utils` (4 eps) | Gravatar, format, litellm config |
| SAML | SSO enterprise (OAuth/LDAP já cobertos) |
| Multi-instância | Redis pub/sub (SignalR backplane), Postgres, storage S3/GCS |
| Comunidade | Integração openwebui.com (share tools/prompts/modelos) |
| Rate limiting | Limites de uso por usuário/modelo |

## Rotas de página (frontend)

| Original (SvelteKit) | Blazor | Status |
|---|---|---|
| `/`, `/c/{id}`, `/auth`, `/s/{id}`, `/error` | idênticos | ✅ |
| `/admin` (users, evals, settings, analytics) | `/admin` (usuários, grupos, analytics, avaliações, flags) | ✅ |
| `/workspace` (models, prompts, knowledge, tools, files) | `/workspace` (abas) | 🟡 sem páginas dedicadas `create`/`edit`/`[id]` |
| `/notes`, `/notes/{id}`, `/notes/new` | `/notes` (editor inline) | 🟡 rotas dedicadas |
| Arquivadas | `/archived` | ✅ |
| `/channels/{id}` | `/channels/{id}` | ✅ |
| `/playground` (+`/completions`,`/images`) | `/playground` | 🟡 sub-páginas |
| `/automations`, `/automations/{id}` | `/automations` | 🟡 detalhe |
| `/calendar` | `/calendar` | ✅ |
| `/folders/{id}` | sidebar | 🟡 rota dedicada |
| `/admin/functions`, `/workspace/functions/*`, `/workspace/skills/*` | — | ⬜ dependem de functions/skills |
| `/watch` | — | ⬜ |

## Decisões de design

- **EF Core Migrations** — `DatabaseMigrator` aplica `Migrate()` no startup e faz
  baseline de bases legadas criadas sem histórico.
- **Streaming SSE** para completions; **SignalR** para realtime multiusuário.
- **Segredos somente no servidor** — UI nunca recebe chaves; tools executam
  server-side com URL oculta.
- **`data/` fora do `wwwroot`** — `openwebui.db` e uploads nunca são servidos
  estaticamente (mesmo padrão `.data/` do agent-harness).
- **`UseForwardedHeaders`** — `X-Forwarded-For/Proto/Host` honrados para
  `redirect_uri` de OAuth e cookies corretos atrás de proxy.
- **Voz/execução client-side** — Web Speech + Web Worker + Pyodide WASM,
  sem infra extra no servidor.
