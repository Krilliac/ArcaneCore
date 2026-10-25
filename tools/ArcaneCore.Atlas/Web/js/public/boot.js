// Entry point of the public page: builds the API client and the socket and mounts the page. The public listener serves this
// page (public.html) and never index.html, whose token gate belongs to the local listener.

import { PublicApi, PublicSocket } from './public-api.js';
import { PublicPage } from './page.js';

const root = document.getElementById('app');
const api = new PublicApi();
const socket = new PublicSocket(api);
const page = new PublicPage(root, {
    api,
    socket,
    schedule: (fn) => requestAnimationFrame(() => fn()),
    observeSize: (element, onSize) => {
        const observer = new ResizeObserver((entries) => {
            const box = entries[0]?.contentRect;
            if (box) {
                onSize(Math.floor(box.width), Math.floor(box.height));
            }
        });
        observer.observe(element);
    },
});
page.start().catch(() => {
    root.textContent = 'The page could not start. Reload to try again.';
});
