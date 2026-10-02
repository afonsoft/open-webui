# SPEC-20261002-passthrough-routers

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `passthrough-routers` |
| Type | `API` |
| Stack | `.NET` |
| Repository | `afonsoft/open-webui` |
| Branch | `feature/devin-20261002-passthrough-routers` |
| Ticket | Issue a criar via create-issues |
| Status | `Approved` |

## 1. User Story

**As a** cliente/integração do Open WebUI
**I want** endpoints `/ollama/*` e `/openai/*` gerenciados pelo servidor
**So that** SDKs/ferramentas usam o Open WebUI como gateway com auth, como no upstream.

**Problem context:**
Upstream expõe `ollama.py` (45 eps) e `openai.py` (15 eps): passthrough autenticado para providers — `/ollama/api/{tags,show,chat,generate,embeddings,version}`, pull/delete/copy/blobs, `/openai/{models,chat/completions,embeddings,images,audio}`. Hoje só usamos providers internamente.

## 2. Scope

**In scope:**
- `GET /ollama/api/tags`, `GET /ollama/api/version`, `POST /ollama/api/{show,chat,generate,embed}` — proxy ao Ollama configurado com auth do app.
- `GET /openai/models`, `POST /openai/{chat/completions,embeddings}` — proxy à conexão OpenAI configurada.
- Resolução de provider por `ConnectionsConfig` existente; stream/SSE preservado.
- Respostas passam sem re-modelagem (transparent proxy); auth JWT/sk-* obrigatória.

**Out of scope:**
- `POST /ollama/api/{pull,create,delete,copy}` + `/blobs/*` (gerenciamento de modelos — fase 2, mais sensível); `/openai/audio|images` (já há endpoints nativos); multi-backend load-balance ponderado.

## 3. Technical Context

**Where:** `Api` (`OllamaPassthroughEndpoints`, `OpenAiPassthroughEndpoints`), `Infrastructure` (`ProviderProxyService` com HttpClient streaming).

**Files to read:**
- `src/OpenWebUI.Infrastructure/Services/ProviderService.cs`
- `src/OpenWebUI.Api/Endpoints/ApiEndpoints.cs` (completions existentes)

**Files to create/modify:**
```text
src/OpenWebUI.Api/Endpoints/{OllamaEndpoints,OpenAiEndpoints}.cs
src/OpenWebUI.Infrastructure/Services/ProviderProxyService.cs
tests/OpenWebUI.Api.Tests/PassthroughTests.cs
```

## 4. Requirements

### RF-001: Ollama proxy
- **Description:** Lista de endpoints suportados re-escrita para `ConnectionsConfig.OllamaUrls[0]`; body/headers de auth aplicados; resposta (incl. NDJSON stream) repassada.
- **Rules:** sem Ollama configurado → `503`; path fora da allowlist → `404`.
- **Input → Output:** `/ollama/api/*` → resposta do provider

### RF-002: OpenAI proxy
- **Description:** Idem para `OpenAiUrls` + key mascarada server-side.
- **Input → Output:** `/openai/*` → resposta do provider

### RF-003: Auth
- **Description:** ambos exigem JWT ou `sk-*` válido.
- **Rules:** nunca logar bodies com chaves.

**Invariants:** chaves de provider nunca saem; SSE/NDJSON streaming sem buffering completo.

## 5. API Contract

`GET /ollama/api/{tags,version}` · `POST /ollama/api/{show,chat,generate,embed}` · `GET /openai/models` · `POST /openai/{chat/completions,embeddings}`
**Auth:** Bearer

## 6. Critérios de Aceite

- [ ] `/ollama/api/tags` devolve resposta do mock Ollama.
- [ ] Streaming `/openai/chat/completions` preserva SSE.
- [ ] Sem provider → `503`; path não permitido → `404`.
- [ ] Testes NUnit com HttpListener.

## 7. Notas

Upstream suporta múltiplas conexões indexadas (`/ollama/{idx}/api/*`) — implementar índice opcional na rota se trivial, senão só a primeira conexão (documentar).
