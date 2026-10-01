window.openwebui = {
	setTheme: function (theme) {
		document.documentElement.dataset.theme = theme;
	},
	scrollToEnd: function (element) {
		if (element) {
			element.scrollTop = element.scrollHeight;
		}
	}
};
