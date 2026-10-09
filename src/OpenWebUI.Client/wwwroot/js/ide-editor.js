// Lazy loader + facade for the vendored CodeMirror bundle (ide-editor.bundle.js).
// Loaded via <script src="js/ide-editor.js"> in index.html; the heavy bundle is
// only fetched on first use (route /ide), keeping the chat boot cheap.
// Globals used by Blazor JS interop: owuiIde.{create,getDoc,setDoc,...}.
(function () {
    let loading = null;
    function ensureBundle() {
        if (window.owuiIdeEditor) {
            return Promise.resolve();
        }
        if (!loading) {
            loading = new Promise(function (resolve, reject) {
                const s = document.createElement("script");
                s.src = "js/ide-editor.bundle.js";
                s.onload = resolve;
                s.onerror = () => reject(new Error("ide editor bundle failed to load"));
                document.head.appendChild(s);
            });
        }
        return loading;
    }
    async function call(name, args) {
        await ensureBundle();
        return window.owuiIdeEditor[name].apply(window.owuiIdeEditor, args);
    }
    window.owuiIde = {
        create: (elId, opts, dotnet) => call("create", [elId, opts, dotnet]),
        getDoc: (elId) => call("getDoc", [elId]),
        setDoc: (elId, doc, path) => call("setDoc", [elId, doc, path]),
        setLanguage: (elId, path) => call("setLanguage", [elId, path]),
        setReadonly: (elId, v) => call("setReadonly", [elId, v]),
        setDark: (elId, dark) => call("setDark", [elId, dark]),
        openSearch: (elId) => call("openSearch", [elId]),
        focus: (elId) => call("focus", [elId]),
        destroy: (elId) => call("destroy", [elId]),
    };
})();
