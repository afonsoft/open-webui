# E2E Testing — Open WebUI (.NET)

## Como subir a app

```bash
# Dev
dotnet run --project src/OpenWebUI.Api   # http://localhost:8080

# Docker (idêntico ao upstream, :3000)
docker build -t openwebui-dotnet:local .
docker run -d -p 3000:8080 \
  -e OLLAMA_BASE_URL=http://host.docker.internal:11434 \
  --add-host host.docker.internal:host-gateway \
  openwebui-dotnet:local
```

## Provider sem LLM real (mock Ollama)

O botão de enviar fica desabilitado sem modelos — é preciso um provider alcançável.

Servir no host:

- `GET /api/tags` → `{"models":[{"name":"mock:latest"}]}`
- `POST /api/chat` → stream NDJSON `{"message":{"content":"..."},"done":false}` ... `{"done":true}`

Subir a app com `OLLAMA_BASE_URL=http://host.docker.internal:11434` (o env é
semeado na primeira inicialização, gravado na tabela de conexões).

## Fatos úteis

- O primeiro usuário registrado vira **admin** (sidebar mostra "Administração").
- `dotnet run` e o container Docker usam SQLite separados.
- Auth pode ser JWT ou `Bearer sk-*`.
- Boot WASM depende de `MapStaticAssets()` — smoke test deve verificar que
  `blazor.webassembly#[.{fingerprint}].js` resolve (não basta HTML 200).
