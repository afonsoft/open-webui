---
paths:
  - "**/*.razor"
  - "**/tailwind.input.css"
  - "**/wwwroot/**"
---

# Regras Blazor/Frontend

- Layout deve seguir o componente Svelte upstream correspondente (mesma estrutura de classes).
- Paleta Tailwind v4 oklch `gray-50→950` (incl. `gray-850`), fonte Inter, `@custom-variant dark`.
- Após alterar classes em `.razor`: regenerar CSS —
  `tailwindcss -i src/OpenWebUI.Client/tailwind.input.css -o src/OpenWebUI.Client/wwwroot/css/tailwind.css --minify`
  e commitar a saída.
- Tema: `.dark` no `<html>` via `openwebui.setTheme` JS; persistência em `localStorage webui.theme`; aplicado pre-paint no `index.html`.
- Componentes em `src/OpenWebUI.Client/Components/`; páginas em `Pages/`; layouts em `Layout/`.
- Não referenciar CDN externo de CSS/JS — assets locais em `wwwroot/assets/`.
