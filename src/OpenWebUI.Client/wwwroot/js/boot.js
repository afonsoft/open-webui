// Proxies corporativos podem bloquear assets de boot pela extensão na URL
// (.dat/.wasm/...) ou por sniffing de payload binário. Todo asset de
// _framework que não seja .js passa por uma cadeia de três camadas
// (mesma abordagem do KnowledgeHub/agent-harness):
//   1. /framework-assets/{stem}/{ext}          — sem sufixo bloqueado na URL,
//      bytes idênticos, então o hash de integridade do boot valida nativamente.
//   2. /framework-assets/{stem}/{ext}?enc=b64  — base64 text/plain, derrota
//      content-sniffing; o SHA-256 é verificado no cliente via crypto.subtle.
//   3. defaultUri                              — dev / ambientes sem proxy.
// Se todas falharem, Blazor.start rejeita e #app mostra mensagem legível.
(function () {
    "use strict";

    // O runtime WASM reporta erros não tratados via console.error e exibe
    // #blazor-error-ui, mas nunca renderiza o texto da exceção. Mantemos um
    // buffer dos últimos erros para o link "Details" do banner poder mostrá-los.
    var errorLog = [];
    var MAX_ERROR_LOG = 10;

    function errorText(value) {
        if (value === null || value === undefined) {
            return "(null)";
        }
        return value.stack || value.message || String(value);
    }

    function renderErrorLog(panel) {
        panel.textContent = errorLog.length === 0
            ? "(no error details captured)"
            : errorLog.map(function (e) { return "[" + e.at + "] " + e.text; }).join("\n\n");
    }

    function recordError(value) {
        var text = errorText(value);
        var last = errorLog.at(-1);
        if (last && last.text === text) {
            return; // falha do renderer relança por evento — manter uma cópia
        }
        errorLog.push({ at: new Date().toISOString(), text: text });
        if (errorLog.length > MAX_ERROR_LOG) {
            errorLog.shift();
        }
        var panel = document.querySelector("#blazor-error-ui .error-details");
        if (panel && !panel.hidden) {
            renderErrorLog(panel);
        }
    }

    var originalConsoleError = console.error.bind(console);
    console.error = function () {
        for (const arg of arguments) {
            recordError(arg);
        }
        return originalConsoleError(...arguments);
    };

    window.addEventListener("error", function (event) {
        recordError(event.error || event.message);
    });
    window.addEventListener("unhandledrejection", function (event) {
        recordError(event.reason);
    });

    document.addEventListener("click", function (event) {
        var target = event.target;
        var toggle = target?.closest
            ? target.closest("#blazor-error-ui .error-details-toggle")
            : null;
        if (!toggle) {
            return;
        }
        event.preventDefault();
        var panel = document.querySelector("#blazor-error-ui .error-details");
        if (!panel) {
            return;
        }
        panel.hidden = !panel.hidden;
        if (panel.hidden) {
            toggle.textContent = "Details";
        } else {
            renderErrorLog(panel);
            toggle.textContent = "Hide details";
        }
    });

    var frameworkSegment = "/_framework/";
    var mimeByExt = { wasm: "application/wasm", json: "application/json", js: "text/javascript" };

    function mirrorUrl(stem, ext, enc) {
        var url = new URL("framework-assets/" + stem + "/" + ext, document.baseURI);
        if (enc) {
            url.searchParams.set("enc", enc);
        }
        return url;
    }

    function decodeBase64(text) {
        var clean = text.replace(/\s+/g, "");
        if (typeof Uint8Array.fromBase64 === "function") {
            return Uint8Array.fromBase64(clean);
        }
        var bin = atob(clean);
        var bytes = new Uint8Array(bin.length);
        for (var i = 0; i < bin.length; i++) {
            bytes[i] = bin.codePointAt(i);
        }
        return bytes;
    }

    // A integridade do manifesto de boot é "sha256-<base64>" (pode ser lista);
    // o payload b64 transforma os bytes no fio, então o browser não consegue
    // conferir — revalidamos os bytes decodificados antes de usar.
    function verifyIntegrity(bytes, integrity) {
        if (!integrity) {
            return Promise.resolve(true);
        }
        var tokens = integrity.split(/\s+/);
        var hash = null;
        for (const token of tokens) {
            if (token.indexOf("sha256-") === 0) {
                hash = token.substring(7);
                break;
            }
        }
        if (hash === null || !window.crypto || !crypto.subtle) {
            return Promise.resolve(false);
        }
        return crypto.subtle.digest("SHA-256", bytes).then(function (buf) {
            var digest = new Uint8Array(buf);
            var bin = "";
            for (const byte of digest) {
                bin += String.fromCodePoint(byte);
            }
            return btoa(bin) === hash;
        });
    }

    function fetchEncoded(url, ext, integrity) {
        return fetch(url).then(function (response) {
            if (!response.ok) {
                throw new Error("b64 mirror " + response.status);
            }
            return response.text();
        }).then(function (text) {
            var bytes = decodeBase64(text);
            return verifyIntegrity(bytes, integrity).then(function (ok) {
                if (!ok) {
                    throw new Error("integrity mismatch for " + url.pathname);
                }
                var headers = new Headers();
                headers.set("Content-Type", mimeByExt[ext] || "application/octet-stream");
                return new Response(bytes, { headers: headers });
            });
        });
    }

    function loadAsset(defaultUri, integrity, stem, ext) {
        var init = integrity ? { integrity: integrity } : {};
        var raw = mirrorUrl(stem, ext, null);
        var encoded = function () {
            return fetchEncoded(mirrorUrl(stem, ext, "b64"), ext, integrity);
        };
        return fetch(raw, init).then(
            function (response) {
                return response.ok ? response : encoded();
            },
            encoded
        ).catch(function () {
            return fetch(defaultUri, init);
        });
    }

    function showBootError() {
        var app = document.getElementById("app");
        if (!app) {
            return;
        }
        app.innerHTML =
            '<div style="max-width:36rem;margin:4rem auto;padding:0 1rem;font-family:sans-serif">' +
            '<h1 style="font-size:1.25rem">Não foi possível iniciar o aplicativo</h1>' +
            '<p>A rede ou o proxy corporativo bloqueou arquivos necessários ao ' +
            'carregamento (filtro de downloads por tipo de mídia).</p>' +
            '<p>Recarregue a página. Se o problema persistir, contate o suporte ' +
            'de TI ou acesse por outra rede.</p>' +
            '<p><a href=".">Recarregar</a></p></div>';
    }

    try {
        Blazor.start({
            loadBootResource: function (type, name, defaultUri, integrity) {
                try {
                    var path = new URL(defaultUri, document.baseURI).pathname;
                    if (!path.includes(frameworkSegment) || path.endsWith(".js")) {
                        return null; // carregamento padrão — proxies liberam .js
                    }
                    var fileName = path.substring(path.lastIndexOf("/") + 1);
                    var dot = fileName.lastIndexOf(".");
                    if (dot <= 0) {
                        return null; // sem extensão para separar — padrão
                    }
                    return loadAsset(
                        defaultUri,
                        integrity,
                        fileName.substring(0, dot),
                        fileName.substring(dot + 1));
                } catch {
                    return null;
                }
            }
        }).catch(showBootError);
    } catch {
        showBootError();
    }
})();
