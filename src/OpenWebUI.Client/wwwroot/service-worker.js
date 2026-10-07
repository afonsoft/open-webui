// Service worker do shell PWA.
// - Documento/navegações: network-first (evita o loop de reload do upstream;
//   index.html sai com Cache-Control: no-cache, então sempre revalida).
// - Assets estáticos (fingerprinted do MapStaticAssets): cache-first.
// - API / realtime: nunca cacheia — erros de rede propagam reais.
const CACHE = 'openwebui-shell-v1';

self.addEventListener('install', () => self.skipWaiting());

self.addEventListener('activate', (event) => {
  event.waitUntil(
    caches
      .keys()
      .then((keys) =>
        Promise.all(keys.filter((k) => k !== CACHE).map((k) => caches.delete(k)))
      )
      .then(() => self.clients.claim())
  );
});

self.addEventListener('fetch', (event) => {
  const { request } = event;
  const url = new URL(request.url);

  if (request.method !== 'GET' || url.origin !== self.location.origin) return;
  if (url.pathname.startsWith('/api/') || url.pathname.startsWith('/ws')) return;

  if (request.mode === 'navigate') {
    event.respondWith(
      fetch(request)
        .then((response) => {
          const copy = response.clone();
          caches.open(CACHE).then((cache) => cache.put('/', copy));
          return response;
        })
        .catch(() =>
          caches.match(request).then((hit) => hit || caches.match('/'))
        )
    );
    return;
  }

  event.respondWith(
    caches.match(request).then((hit) => {
      if (hit) return hit;
      return fetch(request)
        .then((response) => {
          if (response.ok) {
            const copy = response.clone();
            caches.open(CACHE).then((cache) => cache.put(request, copy));
          }
          return response;
        })
        // Falha de rede num asset: resolve com erro em vez de rejeitar —
        // evita "Uncaught (in promise) TypeError: Failed to fetch" no console.
        .catch(() => new Response(null, { status: 504, statusText: 'Gateway Timeout' }));
    })
  );
});

// SPEC-20261007-chat-notifications RF-004: Web Push — entrega run.completed
// mesmo com todas as abas fechadas. O clique foca uma aba existente ou abre
// /c/{chatId}.
self.addEventListener('push', (event) => {
  if (!event.data) return;

  let data;
  try {
    data = event.data.json();
  } catch (e) {
    data = { title: '', status: 'completed' };
  }

  const title = data.title && data.title.length > 0 ? data.title : 'Conversa';
  const body =
    data.status === 'completed' ? (data.snippet || 'Resposta concluída')
    : data.status === 'stopped' ? 'Resposta interrompida'
    : data.status === 'interrupted' ? 'Interrompida — abra para retomar'
    : 'Falhou';

  event.waitUntil(
    self.registration.showNotification('Open WebUI — ' + title, {
      body,
      tag: 'openwebui-chat-' + (data.runId || ''),
      data: { url: data.url || '/' },
    })
  );
});

self.addEventListener('notificationclick', (event) => {
  event.notification.close();
  const url = (event.notification.data && event.notification.data.url) || '/';
  event.waitUntil(
    self.clients
      .matchAll({ type: 'window', includeUncontrolled: true })
      .then((clientList) => {
        // Reutiliza uma aba aberta quando existe — senão abre uma nova.
        for (const client of clientList) {
          if ('focus' in client) {
            client.focus();
            client.navigate(url);
            return;
          }
        }
        return self.clients.openWindow(url);
      })
  );
});
