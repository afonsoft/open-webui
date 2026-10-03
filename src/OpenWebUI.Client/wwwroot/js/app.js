window.openwebui = {
	setTheme: function (theme) {
		const isDark =
			theme === 'dark' ||
			(theme === 'system' && window.matchMedia('(prefers-color-scheme: dark)').matches);
		document.documentElement.classList.toggle('dark', isDark);
		document.documentElement.dataset.theme = isDark ? 'dark' : 'light';
	},
	scrollToEnd: function (element) {
		if (element) {
			element.scrollTop = element.scrollHeight;
		}
	},
	copyText: function (text) {
		return navigator.clipboard ? navigator.clipboard.writeText(text) : Promise.resolve();
	},
	prompt: function (message, defaultValue) {
		return window.prompt(message, defaultValue || '');
	},
	confirm: function (message) {
		return window.confirm(message);
	},
	openFilePicker: function (element) {
		if (element) {
			element.click();
		}
	},
	autoResize: function (element) {
		if (element) {
			element.style.height = 'auto';
			element.style.height = Math.min(element.scrollHeight, 200) + 'px';
		}
	},
	// Protocolo upstream: abre https://openwebui.com/post?type=<type> em nova aba e
	// posta o item serializado quando a página sinaliza 'loaded'.
	shareCommunity: function (type, payloadJson) {
		const url = 'https://openwebui.com';
		const tab = window.open(url + '/post?type=' + encodeURIComponent(type), '_blank');
		if (!tab) return false;
		const handler = function (event) {
			if (event.origin !== url) return;
			if (event.data === 'loaded') {
				tab.postMessage(payloadJson, '*');
				window.removeEventListener('message', handler);
			}
		};
		window.addEventListener('message', handler, false);
		return true;
	},
	setLang: function (lang) {
		document.documentElement.lang = lang || 'pt-BR';
	},
	// Trap de foco para modais acessíveis: foca o primeiro interativo e faz
	// Tab/Shift+Tab circular dentro do elemento (Escape fica no .razor).
	trapFocus: function (element) {
		if (!element) return;
		const selector = 'button,[href],input,select,textarea,[tabindex]:not([tabindex="-1"])';
		const focusables = function () {
			return Array.prototype.filter.call(
				element.querySelectorAll(selector),
				function (el) { return !el.disabled && el.offsetParent !== null; });
		};
		element._openwebuiTrap = function (e) {
			if (e.key !== 'Tab') return;
			const f = focusables();
			if (!f.length) return;
			if (e.shiftKey && document.activeElement === f[0]) {
				f[f.length - 1].focus();
				e.preventDefault();
			} else if (!e.shiftKey && document.activeElement === f[f.length - 1]) {
				f[0].focus();
				e.preventDefault();
			}
		};
		element.addEventListener('keydown', element._openwebuiTrap);
		const first = focusables()[0];
		if (first) first.focus();
	},
	releaseFocus: function (element) {
		if (element && element._openwebuiTrap) {
			element.removeEventListener('keydown', element._openwebuiTrap);
			element._openwebuiTrap = null;
		}
	}
};
