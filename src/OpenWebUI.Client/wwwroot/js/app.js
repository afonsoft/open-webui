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
	}
};
