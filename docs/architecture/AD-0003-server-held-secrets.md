# AD-0003 — Segredos somente no servidor

## Context

Chaves de providers (OpenAI, images), segredo JWT e URLs de execução de tools
são credenciais que não podem vazar para o navegador — o cliente WASM é código
público por natureza.

## Decision

- `ConfigService` persiste segredos na tabela `config` (kv) no servidor.
- Respostas de config expõem apenas *flags* (`OpenAiKeyConfigured`), nunca o valor.
- Tools HTTP: `url` de execução fica no servidor; o cliente só recebe
  `has_url` e o tool-loop roda dentro do endpoint de completions.
- JWT secret gerado na 1ª execução e persistido em `webui.jwt.secret`
  (mesmo padrão do `WEBUI_SECRET_KEY` do upstream).

## Consequences

- Positive: nenhum segredo chega ao WASM/rede; rotação via admin sem rebuild.
- Trade-off: segredos em repouso no SQLite — recomenda-se volume com
  permissões restritas e backup cifrado (mesmo modelo do upstream).
