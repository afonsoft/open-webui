#!/usr/bin/env python3
"""Importa locales do upstream open-webui para o formato do cliente .NET.

O cliente usa chaves simbólicas (admin.tab_chats) com valores em inglês como
fallback; o upstream usa a própria frase em inglês como chave. O script cruza:
para cada chave nossa, o valor en-US é consultado no translation.json do
upstream; sem correspondência, o valor en-US é mantido (o fallback por chave
cobre o resto).

Uso:
    python3 scripts/import-locales.py \
        --en src/OpenWebUI.Client/wwwroot/i18n/en-US.json \
        --upstream-dir /caminho/para/locales \
        --out-dir src/OpenWebUI.Client/wwwroot/i18n \
        --locales es-ES,fr-FR,de-DE,it-IT,ja-JP,zh-CN
"""

import argparse
import json
import pathlib


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--en", required=True, help="caminho do en-US.json")
    parser.add_argument("--upstream-dir", required=True,
                        help="diretório com {locale}.json do upstream (frase EN como chave)")
    parser.add_argument("--out-dir", required=True, help="diretório de saída")
    parser.add_argument("--locales", required=True, help="lista separada por vírgula")
    args = parser.parse_args()

    en = json.loads(pathlib.Path(args.en).read_text(encoding="utf-8"))
    out_dir = pathlib.Path(args.out_dir)
    out_dir.mkdir(parents=True, exist_ok=True)

    for locale in args.locales.split(","):
        locale = locale.strip()
        upstream_path = pathlib.Path(args.upstream_dir) / f"{locale}.json"
        upstream = json.loads(upstream_path.read_text(encoding="utf-8"))
        result, matched = {}, 0
        for key, en_value in en.items():
            translated = upstream.get(en_value)
            if isinstance(translated, str) and translated:
                result[key] = translated
                matched += 1
            else:
                result[key] = en_value
        (out_dir / f"{locale}.json").write_text(
            json.dumps(result, ensure_ascii=False, indent=2) + "\n",
            encoding="utf-8")
        print(f"{locale}: {matched}/{len(en)} chaves traduzidas")


if __name__ == "__main__":
    main()
