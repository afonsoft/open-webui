# SPEC-20261002-notifications-webhooks

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `notifications-webhooks` |
| Type | `Feature` |
| Stack | `.NET` |
| Repository | `afonsoft/open-webui` |
| Branch | `feature/devin-20261002-notifications-webhooks` |
| Ticket | Issue a criar via create-issues |
| Status | `Approved` |

## 1. User Story

**As a** admin do Open WebUI
**I want** configurar webhooks de notificação (novo usuário pendente, aprovação, erros de automação)
**So that** recebo alertas fora da aplicação, como no upstream `notifications.py`.

**Problem context:**
Upstream tem 7 endpoints de notifications (webhook URL por usuário/admin + teste). Nosso port não tem nenhum disparo externo.

## 2. Scope

**In scope:**
- `NotificationWebhook` (user-scoped + admin-scoped): url, events habilitados, enabled.
- Eventos: `user.pending`, `user.approved`, `automation.failed`, `channel.mention` (se trivial).
- `POST /api/v1/notifications/webhook` config por usuário; `.../admin` para global (admin-only); `POST .../test` dispara payload de teste.
- Dispatcher `NotificationService` com fila best-effort (não bloqueia request; log de falha).
- Assinatura: header `X-Webhook-Event` + `X-Webhook-Signature` HMAC com segredo por webhook.

**Out of scope:**
- Retry com backoff persistente (best-effort + log na v1); templates customizáveis; digest.

## 3. Technical Context

**Where:** `Domain` (NotificationWebhook), `Infrastructure` (NotificationService com HttpClient), `Api` (`NotificationEndpoints` + chamadas no fluxo de signup/approval/automations), `Client` (campo webhook URL em Settings).

**Files to read:**
- `src/OpenWebUI.Api/Endpoints/{UserEndpoints,AutomationEndpoints}.cs`
- `src/OpenWebUI.Infrastructure/Services/AutomationService.cs`
- `src/OpenWebUI.Infrastructure/Services/ToolExecutor.cs` (padrão HttpClient externo)

**Files to create/modify:**
```text
src/OpenWebUI.Domain/Entities/NotificationWebhook.cs
src/OpenWebUI.Application/Contracts/NotificationContracts.cs
src/OpenWebUI.Infrastructure/Services/NotificationService.cs
src/OpenWebUI.Api/Endpoints/NotificationEndpoints.cs
tests/OpenWebUI.Api.Tests/NotificationEndpointsTests.cs
```

## 4. Requirements

### RF-001: Config
- **Description:** webhook por usuário (`{url, events[]}`) e global admin; máscara na leitura; segredo HMAC gerado no create.
- **Input → Output:** `{url, events}` → webhook `{id, url_masked, events}`

### RF-002: Dispatch
- **Description:** Eventos publicam POST JSON `{event, data, ts}` com headers de assinatura; falha → log warning, nunca quebra request.
- **Input → Output:** evento interno → POST externo best-effort

### RF-003: Teste
- **Description:** `POST .../test` envia `{event:"test"}` e retorna status HTTP do destino.
- **Input → Output:** — → `{status_code, ok}`

**Invariants:** URLs validadas (http/https; bloquear RFC1918? `[A DEFINIR]` — upstream não bloqueia); secrets assinam mas nunca retornam.

## 5. API Contract

`GET|POST|DELETE /api/v1/notifications/webhook` · `POST /api/v1/notifications/webhook/test` · variantes `/admin/*`
**Auth:** Bearer · admin scope admin-only

## 6. Critérios de Aceite

- [ ] Webhook recebe payload assinado no evento configurado.
- [ ] Falha do destino não afeta o fluxo originário.
- [ ] Test endpoint reflete status real do destino.
- [ ] Testes NUnit com HttpListener.

## 7. Notas

SSRF: avaliar blocklist de IPs internos (link-local, loopback) — upstream permite, mas recomendo validação com opt-out por config.
