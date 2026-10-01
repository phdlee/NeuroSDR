const CACHE = "neurosdr-remote-v10-desktop";
const ASSETS = [
  "/",
  "/index.html",
  "/desktop.css",
  "/js/main.js",
  "/js/hub.js",
  "/js/spectrum.js",
  "/js/audio.js",
  "/js/types.js",
  "/manifest.webmanifest"
];

self.addEventListener("install", (event) => {
  event.waitUntil(caches.open(CACHE).then((cache) => cache.addAll(ASSETS)).then(() => self.skipWaiting()));
});

self.addEventListener("activate", (event) => {
  event.waitUntil(
    caches.keys().then((keys) => Promise.all(keys.filter((k) => k !== CACHE).map((k) => caches.delete(k)))).then(() => self.clients.claim())
  );
});

self.addEventListener("fetch", (event) => {
  const url = new URL(event.request.url);
  if (url.pathname.startsWith("/hubs/") || url.pathname.startsWith("/api/")) return;
  const shell = url.pathname === "/" || url.pathname === "/index.html"
    || url.pathname === "/desktop.css" || url.pathname.startsWith("/js/");
  if (shell) {
    event.respondWith(
      fetch(event.request).then((res) => {
        const copy = res.clone();
        caches.open(CACHE).then((cache) => cache.put(event.request, copy));
        return res;
      }).catch(() => caches.match(event.request))
    );
    return;
  }
  event.respondWith(
    caches.match(event.request).then((cached) => cached || fetch(event.request).then((res) => {
      const copy = res.clone();
      caches.open(CACHE).then((cache) => cache.put(event.request, copy));
      return res;
    }).catch(() => cached))
  );
});
