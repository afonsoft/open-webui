// Execução de código em blocos markdown (JavaScript via Web Worker, Python via Pyodide WASM).
window.openwebui = window.openwebui || {};

window.openwebui.codeExec = (function () {
	const JS_TIMEOUT_MS = 10000;
	const PYODIDE_CDN = 'https://cdn.jsdelivr.net/pyodide/v0.27.7/full/';
	const RUNNABLE = {
		javascript: 'js',
		js: 'js',
		python: 'python',
		py: 'python'
	};
	const LABELS = {
		js: 'JavaScript',
		python: 'Python'
	};

	let pyWorker = null;
	let pyReady = null;

	// Injeta o botão "Executar" em blocos de código executáveis dentro de root.
	function enhance(root) {
		if (!root) {
			return;
		}
		root.querySelectorAll('pre > code').forEach(function (codeEl) {
			const pre = codeEl.parentElement;
			if (pre.dataset.codeexec) {
				return;
			}
			const lang = detectLang(codeEl);
			if (!lang) {
				return;
			}
			pre.dataset.codeexec = '1';
			pre.classList.add('relative');

			const button = document.createElement('button');
			button.type = 'button';
			button.textContent = '▶ Executar';
			button.title = 'Executar ' + LABELS[lang] + ' no navegador';
			button.className =
				'absolute top-1.5 right-1.5 px-2 py-1 text-[0.7rem] font-medium rounded-md ' +
				'bg-gray-100 dark:bg-gray-800 text-gray-600 dark:text-gray-300 ' +
				'hover:bg-gray-200 dark:hover:bg-gray-700 transition-colors cursor-pointer';
			button.addEventListener('click', function () {
				run(codeEl.innerText, lang, button, pre);
			});
			pre.appendChild(button);
		});
	}

	function detectLang(codeEl) {
		const cls = Array.from(codeEl.classList).find(function (c) {
			return c.startsWith('language-');
		});
		if (!cls) {
			return null;
		}
		return RUNNABLE[cls.substring('language-'.length).toLowerCase()] || null;
	}

	function outputPanel(pre) {
		let panel = pre.nextElementSibling;
		if (!panel || !panel.classList.contains('codeexec-out')) {
			panel = document.createElement('div');
			panel.className =
				'codeexec-out mt-1 rounded-lg border border-gray-200 dark:border-gray-800 ' +
				'bg-gray-50 dark:bg-gray-900 px-3 py-2 text-[0.8rem] font-mono whitespace-pre-wrap';
			pre.parentNode.insertBefore(panel, pre.nextSibling);
		}
		return panel;
	}

	async function run(code, lang, button, pre) {
		const panel = outputPanel(pre);
		button.disabled = true;
		button.classList.add('opacity-50');
		const started = performance.now();
		panel.textContent = lang === 'python' ? 'Carregando Python (Pyodide)…' : 'Executando…';

		try {
			const result =
				lang === 'js' ? await runJs(code) : await runPython(code, panel);
			const elapsed = ((performance.now() - started) / 1000).toFixed(1);
			renderResult(panel, result, elapsed);
		} catch (err) {
			panel.textContent = 'Erro: ' + (err && err.message ? err.message : String(err));
		} finally {
			button.disabled = false;
			button.classList.remove('opacity-50');
		}
	}

	function renderResult(panel, result, elapsed) {
		panel.textContent = '';
		if (result.stdout) {
			const out = document.createElement('div');
			out.textContent = result.stdout;
			panel.appendChild(out);
		}
		if (result.stderr) {
			const err = document.createElement('div');
			err.className = 'text-red-600 dark:text-red-400';
			err.textContent = result.stderr;
			panel.appendChild(err);
		}
		if (!result.stdout && !result.stderr) {
			const empty = document.createElement('div');
			empty.className = 'text-gray-500 dark:text-gray-400';
			empty.textContent = '(sem saída)';
			panel.appendChild(empty);
		}
		const meta = document.createElement('div');
		meta.className = 'mt-1 text-[0.7rem] text-gray-400 dark:text-gray-500';
		meta.textContent = elapsed + 's';
		panel.appendChild(meta);
	}

	// ---------------- JavaScript ----------------

	function runJs(code) {
		return new Promise(function (resolve, reject) {
			const workerSource =
				'const stdout=[];const stderr=[];' +
				'["log","info","debug"].forEach(m=>{console[m]=(...a)=>stdout.push(a.map(String).join(" "));});' +
				'["warn","error"].forEach(m=>{console[m]=(...a)=>stderr.push(a.map(String).join(" "));});' +
				'onmessage=function(e){try{const r=eval(e.data);' +
				'if(r!==undefined)stdout.push(String(r));' +
				'postMessage({stdout:stdout.join("\\n"),stderr:stderr.join("\\n")});' +
				'}catch(err){stderr.push(String(err));' +
				'postMessage({stdout:stdout.join("\\n"),stderr:stderr.join("\\n")});}};';

			const worker = new Worker(URL.createObjectURL(new Blob([workerSource], { type: 'text/javascript' })));
			const timer = setTimeout(function () {
				worker.terminate();
				resolve({ stdout: '', stderr: 'Execução abortada: timeout de ' + JS_TIMEOUT_MS / 1000 + 's' });
			}, JS_TIMEOUT_MS);

			worker.onmessage = function (e) {
				clearTimeout(timer);
				worker.terminate();
				resolve(e.data);
			};
			worker.onerror = function (e) {
				clearTimeout(timer);
				worker.terminate();
				reject(new Error(e.message || 'erro no worker'));
			};
			worker.postMessage(code);
		});
	}

	// ---------------- Python (Pyodide) ----------------

	function ensurePyWorker() {
		if (pyWorker) {
			return Promise.resolve(pyWorker);
		}
		if (pyReady) {
			return pyReady;
		}
		pyReady = new Promise(function (resolve, reject) {
			const worker = new Worker('js/py-worker.js');
			const timer = setTimeout(function () {
				reject(new Error('timeout carregando Pyodide'));
			}, 60000);
			worker.onmessage = function (e) {
				if (e.data.type === 'ready') {
					clearTimeout(timer);
					pyWorker = worker;
					resolve(worker);
				} else if (e.data.type === 'load-error') {
					clearTimeout(timer);
					pyReady = null;
					reject(new Error(e.data.error || 'falha ao carregar Pyodide'));
				}
			};
			worker.onerror = function (e) {
				clearTimeout(timer);
				pyReady = null;
				reject(new Error(e.message || 'falha ao carregar Pyodide'));
			};
			worker.postMessage({ type: 'init', indexURL: PYODIDE_CDN });
		});
		return pyReady;
	}

	async function runPython(code, panel) {
		const worker = await ensurePyWorker();
		panel.textContent = 'Executando…';
		return new Promise(function (resolve) {
			const timer = setTimeout(function () {
				// Reinicia o worker para abortar a execução travada.
				worker.terminate();
				pyWorker = null;
				pyReady = null;
				resolve({ stdout: '', stderr: 'Execução abortada: timeout de 30s' });
			}, 30000);
			const handler = function (e) {
				if (e.data.type === 'result') {
					clearTimeout(timer);
					worker.removeEventListener('message', handler);
					resolve(e.data);
				}
			};
			worker.addEventListener('message', handler);
			worker.postMessage({ type: 'run', code: code });
		});
	}

	return { enhance: enhance };
})();
