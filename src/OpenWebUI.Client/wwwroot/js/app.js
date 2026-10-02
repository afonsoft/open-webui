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
	}
};
