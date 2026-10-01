// Áudio do Open WebUI — STT (Web Speech API) e TTS (speechSynthesis).
// Degrada silenciosamente quando o navegador não suporta.
(function () {
	const SR = window.SpeechRecognition || window.webkitSpeechRecognition;
	let recog = null;
	let sttRef = null;
	let accFinal = '';
	let ttsRef = null;

	const VOICE_KEY = 'webui.audio.voice';
	const AUTOSEND_KEY = 'webui.audio.autosend';

	function cleanForSpeech(text) {
		return (text || '')
			.replace(/```[\s\S]*?```/g, ' ')
			.replace(/`([^`]*)`/g, '$1')
			.replace(/!\[[^\]]*\]\([^)]*\)/g, ' ')
			.replace(/\[([^\]]*)\]\([^)]*\)/g, '$1')
			.replace(/[#>*_~|]/g, ' ')
			.replace(/\s+/g, ' ')
			.trim();
	}

	function pickVoice() {
		if (!('speechSynthesis' in window)) return null;
		const voices = speechSynthesis.getVoices() || [];
		const saved = localStorage.getItem(VOICE_KEY);
		if (saved) {
			const v = voices.find((x) => x.name === saved);
			if (v) return v;
		}
		const lang = document.documentElement.lang || 'pt-BR';
		return (
			voices.find((v) => v.lang === lang) ||
			voices.find((v) => v.lang.startsWith(lang.slice(0, 2))) ||
			null
		);
	}

	window.openwebui = window.openwebui || {};
	window.openwebui.audio = {
		sttSupported: () => !!SR,
		ttsSupported: () => 'speechSynthesis' in window,

		voices: () =>
			('speechSynthesis' in window ? speechSynthesis.getVoices() : []).map((v) => ({
				name: v.name,
				lang: v.lang,
			})),
		getVoice: () => localStorage.getItem(VOICE_KEY) || '',
		setVoice: (name) =>
			name ? localStorage.setItem(VOICE_KEY, name) : localStorage.removeItem(VOICE_KEY),
		getAutoSend: () => localStorage.getItem(AUTOSEND_KEY) === 'true',
		setAutoSend: (on) => localStorage.setItem(AUTOSEND_KEY, on ? 'true' : 'false'),

		// Inicia o ditado; callbacks no ref: OnSttResult(text), OnSttEnded(), OnSttError(name).
		startStt: (ref, lang) => {
			if (!SR) return false;
			window.openwebui.audio.stopStt();
			sttRef = ref;
			accFinal = '';
			recog = new SR();
			recog.lang = lang || 'pt-BR';
			recog.interimResults = true;
			recog.continuous = false;
			recog.onresult = (e) => {
				let interim = '';
				for (let i = e.resultIndex; i < e.results.length; i++) {
					const r = e.results[i];
					if (r.isFinal) {
						accFinal += (accFinal ? ' ' : '') + r[0].transcript;
					} else {
						interim += r[0].transcript;
					}
				}
				const full = accFinal + (interim ? (accFinal ? ' ' : '') + interim : '');
				sttRef?.invokeMethodAsync('OnSttResult', full);
			};
			recog.onerror = (e) => sttRef?.invokeMethodAsync('OnSttError', e.error || 'error');
			recog.onend = () => {
				const d = sttRef;
				sttRef = null;
				d?.invokeMethodAsync('OnSttEnded');
			};
			try {
				recog.start();
				return true;
			} catch {
				sttRef = null;
				return false;
			}
		},
		stopStt: () => {
			try {
				recog?.stop();
			} catch {
				/* noop */
			}
		},

		// Fala o texto; ref opcional recebe OnTtsEnded() ao terminar.
		speak: (text, ref) => {
			if (!('speechSynthesis' in window)) return false;
			speechSynthesis.cancel();
			ttsRef = ref || null;
			const u = new SpeechSynthesisUtterance(cleanForSpeech(text));
			const v = pickVoice();
			if (v) {
				u.voice = v;
				u.lang = v.lang;
			}
			u.onend = u.onerror = () => {
				const d = ttsRef;
				ttsRef = null;
				d?.invokeMethodAsync('OnTtsEnded');
			};
			speechSynthesis.speak(u);
			return true;
		},
		stopSpeak: () => {
			try {
				speechSynthesis.cancel();
			} catch {
				/* noop */
			}
		},
		isSpeaking: () => 'speechSynthesis' in window && speechSynthesis.speaking,
	};
})();
