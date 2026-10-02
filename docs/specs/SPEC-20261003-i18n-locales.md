# SPEC-20261003-i18n-locales

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `i18n-locales` |
| Type | `Feature` |
| Stack | `.NET` |
| Repository | `afonsoft/open-webui` |
| Branch | `feature/devin-20261003-i18n-locales` |
| Ticket | Issue #88 |
| Status | `Approved` |

## 1. User Story

**As a** usuário internacional
**I want** os ~30 locales do upstream e mensagens do backend localizadas
**So that** a i18n cobre o idioma e o servidor, não só pt-BR/en-US.

**Problem context:**
Cliente hoje: `i18n/pt-BR.json` + `en-US.json` (381 chaves, troca sem reload). Upstream tem ~30 locales; mensagens de erro do backend são pt-BR fixas.

## 2. Scope

**In scope:**
- Locales: extrair traduções do upstream (`static/i18n/*.json` → mapear chaves `.razor` → `wwwroot/i18n/{locale}.json`); começar com es, fr, de, it, ja, zh-CN (top usage) + script para importar o resto.
- Backend: `detail` de erros em `Accept-Language`? Decisão: backend retorna códigos estáveis (`error_code`) + cliente traduz — ADR pequeno.
- Seletor de idioma lista todos os locales disponíveis dinamicamente.

**Out of scope:**
- Tradução de conteúdo de dados do usuário; RTL layout.

## 3. Technical Context

**Files to read:**
- `src/OpenWebUI.Client/Services/LocalizationService.cs`
- `src/OpenWebUI.Client/wwwroot/i18n/`
- upstream `static/i18n/` (fetch do repo open-webui/open-webui)

## 4. Requirements

### RF-001: Pacotes de locale
- **Rules:** chave ausente cai para en-US depois pt-BR; locale JSON inválido não quebra boot.

### RF-002: error_code backend
- **Rules:** endpoints retornam `{detail, error_code}`; cliente mapeia `errors.{code}`.

## 5. API Contract

Sem mudança de contrato — `error_code` aditivo; `GET /i18n/{locale}.json` já servido por static assets.

## 6. Critérios de Aceite

- [ ] 6+ locales importados e navegáveis.
- [ ] Erro de auth aparece traduzido no idioma selecionado.
- [ ] Teste de fallback de chave ausente.

## Delivered

- PR: https://github.com/afonsoft/open-webui/pull/98
- Issue: https://github.com/afonsoft/open-webui/issues/88
- Epic: https://github.com/afonsoft/open-webui/issues/79
