---
name: tailwind-css
description: >
  Regenera o CSS Tailwind v4 do cliente Blazor. Use quando classes utilitárias
  forem alteradas em .razor ou em tailwind.input.css. Do NOT use para editar
  wwwroot/css/tailwind.css manualmente — ele é artefato gerado.
metadata:
  version: "1.0.0"
---

## Contexto

O cliente Blazor usa Tailwind CSS v4 com o tema do upstream Open WebUI
(paleta oklch `gray-50→950` incl. `gray-850`, fonte Inter Variable,
`@custom-variant dark`). A fonte é `src/OpenWebUI.Client/tailwind.input.css`;
a saída gerada é commitada em `src/OpenWebUI.Client/wwwroot/css/tailwind.css`
(sem Node no build .NET nem no Docker).

## Comportamento

1. Editar classes em `.razor` ou o `tailwind.input.css` conforme necessário.
2. Regenerar:
   ```bash
   tailwindcss -i src/OpenWebUI.Client/tailwind.input.css \
     -o src/OpenWebUI.Client/wwwroot/css/tailwind.css --minify
   ```
   (CLI instalado pelo blueprint em `~/.local/bin/tailwindcss`.)
3. Commitar `.razor` + `tailwind.css` juntos.

## Restrições

- Nunca editar `wwwroot/css/tailwind.css` manualmente.
- `@custom-variant dark (&:where(.dark, .dark *))` deve ser preservado — o tema
  depende da classe `.dark` no `<html>`.
- `@plugin '@tailwindcss/typography'` deve permanecer (prose das mensagens).
