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
| Routers (grupos de endpoints) | 33 | ~24 ✅ / ~7 🟡 parcial / ~2 ⬜ | ver tabela |
| Rotas de página | 47 | ~46 | falta apenas edição inline de functions (admin) |
| Engines de integração | ~15 (embeddings, busca web, imagens, TTS, extração) | ~4 | engines alternativas por família |

## Paridade por área (router upstream → .NET)

### Migrado ✅

| Área | Observação |
|---|---|
| `auths` — signup/signin/profile/api-key/admin | JWT, primeiro usuário admin, `sk-*`, aprovação de pendentes |
| `chats` — core | CRUD, busca, pin/archive, pastas, tags, share `/s/{id}`, clone, import/export, editar/regenerar mensagem |
| `users` — core | Perfil, senha, timezone, admin CRUD |
| `models` — custom | CRUD workspace, system prompt + params, toggle |
| `evaluations` — feedbacks | 👍/👎 + lista admin paginada + export + arena battles + leaderboard ELO |
| `files` — upload/serve | Extração de texto, `data/uploads/{user}/` fora do wwwroot |
| `knowledge` — RAG | Coleções, itens, embeddings (Ollama/OpenAI), retrieval por cosseno |
| `channels` — grupo + DM | SignalR `/ws`: `message:new`, `typing`, `presence`, `@modelo` invoca provider; DMs, threads, reações, lidos, pins, access grants por canal |
| `groups` — RBAC | CRUD + membros + flags workspace/sharing/chat |
| `folders`, `memories`, `notes`, `prompts` | CRUD completo |
| `tasks` — LLM | Título, follow-ups, tags automáticas |
| `tools` — HTTP | Function calling com loop server-side (máx. 5), URL nunca exposta |
| `functions`/`pipelines`/`skills` | Registry admin de functions (manifest+valves), servidores de pipelines externos (pipes como `pipeline:{id}`), skills anexáveis a modelos (slice plugin-ecosystem) |
| `images` — OpenAI Images | Geração + config admin + botão no chat |
| `configs` — core | Conexões (chaves mascaradas), admin config, feature flags, export/import |
| `analytics` | Dashboard admin-only |
| `notifications` (7 eps) | Webhooks user/global, eventos (`user.pending`, `user.approved`, `automation.failed`), HMAC `X-Webhook-Signature`, `/test`, campo em Settings |
| `automations` | Agendas (interval/daily/weekly UTC) + runs + run-now + visão calendário |
| `calendar` | Calendários reais (CRUD) + events + access grants |
| Multi-instância | `DATABASE_PROVIDER=postgresql`, `REDIS_URL` backplane SignalR, `IFileStorage` local/S3, `/health` enriquecido |
| OAuth/OIDC + LDAP | Google/GitHub/Microsoft/OIDC + bind LDAP (slice auth-sso-rbac) |
| SAML 2.0 | SP-initiated (HTTP-POST): metadata, login redirect, ACS com validação de assinatura/issuer/audience + JIT user (slice enterprise-sso) |
| `scim` | Users CRUD + Groups + ServiceProviderConfig + filtro `userName eq`, token dedicado (`scim.token`), `active=false` → desativa |
| EF Migrations | `DatabaseMigrator` + baseline de `webui.db` legadas |
| Docker | Dockerfile multi-stage + compose (+ ollama opcional) |

### Parcial 🟡

| Área | Feito | Falta |
|---|---|---|
| `retrieval` (17 eps) | Embeddings + busca vetorial + `/process/{file,text,url,youtube}` + `/process/web/search` (searxng/duckduckgo/tavily/brave) + híbrido BM25 + reset db/uploads | Demais engines de busca (8), reranking por provider, engines de extração de conteúdo |
| `users` (26 eps) | Perfil/admin + user settings + sessões OAuth + busca/paginação/filtros | Permissões granulares por usuário (hoje por grupo) |
| `chats` (50 eps) | Core completo | Versões/diff de mensagens, chat-events realtime, lista admin de todos os chats |
| `knowledge` (35 eps) | RAG essencial | Anexar `file_id` a itens, reindex, batch ops |
| `tools` (15 eps) | Tools HTTP | Valves/user settings por tool (tools em código: decisão documentada no plugin-ecosystem) |
| `ollama`/`openai` passthrough | `/ollama/api/{tags,version,show,chat,generate,embed}` + `/openai/{models,chat/completions,embeddings}` (+ variantes indexadas) | `pull/create/delete/copy` + `blobs/*` (gerenciamento de modelos), audio/images via passthrough |
| `images` (6 eps) | Engines plugáveis: OpenAI, A1111, Gemini, ComfyUI + `/edit` + `/config/engines` + `/config/test` | Variações avançadas por engine |
| `audio` (6 eps) | `POST /audio/speech` (TTS OpenAI-compatible), `POST /transcriptions` (STT openai/deepgram), `/voices`, `/models`, `/capabilities`, fallback Web Speech | Whisper local, engines TTS extras (ElevenLabs/Azure) |
| `configs` (25 eps) | Conexões/flags/admin | OAuth/LDAP toggles, direct connections, configs finas restantes por domínio |
| `groups` (11 eps) | Flags workspace/sharing | Domínios allowlist, permissões granulares por feature |
| `models` (16 eps) | Custom models + arena + access grants (user/group/*) | Model filters |
| `notes` (12 eps) | CRUD | Colaboração realtime (yjs) |
| `terminals` | Terminal servers admin + proxy HTTP/WS + engine jupyter no chat | Spawn do processo Jupyter local, PTY no host — decisão documentada: só proxy externo |
| `i18n` | pt-BR/en-US sem reload | ~30 locales do upstream; backend não traduzido |

### Pendente ⬜

| Área | Escopo upstream |
|---|---|
| `utils` (4 eps) | Gravatar, code format, litellm config |
| Comunidade | Integração openwebui.com (share tools/prompts/modelos) |
| Rate limiting | Limites de uso por usuário/modelo |

## Rotas de página (frontend)

| Original (SvelteKit) | Blazor | Status |
|---|---|---|
| `/`, `/c/{id}`, `/auth`, `/s/{id}`, `/error` | idênticos | ✅ |
| `/admin` (users, evals, settings, analytics) | `/admin` (usuários, grupos, analytics, avaliações, flags) | ✅ |
| `/workspace` (models, prompts, knowledge, tools, files) + `create`/`{id}` | `/workspace` + rotas dedicadas `create`/`{id}` | ✅ |
| `/notes`, `/notes/{id}`, `/notes/new` | idênticos | ✅ |
| Arquivadas | `/archived` | ✅ |
| `/channels/{id}` | `/channels/{id}` | ✅ |
| `/playground` (+`/completions`,`/images`) | `/playground`, `/playground/completions`, `/playground/images` | ✅ |
| `/automations`, `/automations/{id}` | idênticos | ✅ |
| `/calendar` | `/calendar` | ✅ |
| `/folders/{id}` | `/folders/{id}` | ✅ |
| `/admin/functions`, `/workspace/functions/*`, `/workspace/skills/*` | admin Functions/Pipelines + `/workspace/skills` (aba) | 🟡 functions por aba admin (edição inline, sem rotas dedicadas) |
| `/watch` | `/watch?v=` | ✅ |

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
