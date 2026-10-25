// The public page: a live top-down chart of the maps the operator chose, sign-in for account holders, and the few things a
// visitor may do (find by name, click for basic information, jump to a bookmark, list one's own characters). It talks to the
// server only through PublicApi and PublicSocket and renders only what MapModel accepted from a projected frame.

import { fill, h } from '../util.js';
import { LoginForm } from './login.js';
import { PublicApiError } from './public-api.js';
import { Camera, FALLBACK_LAYERS, MapModel, drawMap, hitTest, initialLayers } from './map.js';

export const SECURITY_NAMES = ['Player', 'Moderator', 'Game Master', 'Administrator'];
const FIND_PATTERN = /^[A-Za-z0-9 _'-]{1,32}$/;
const DRAG_THRESHOLD = 4;

const kindTitle = { character: 'Character', creature: 'Creature', gameobject: 'Object' };

/** What the status line says for the socket and the map's state. */
export function statusText({ socketState, model, loginAvailable }) {
    if (socketState === 'connecting') {
        return { text: 'Connecting…', level: 'info' };
    }
    if (socketState === 'closed') {
        return { text: 'Connection lost. Reconnecting…', level: 'warn' };
    }
    if (model.detail === 'busy') {
        return { text: 'Every live feed is in use right now. This map will appear when one is free.', level: 'warn' };
    }
    if (!model.connected || !model.hasFrame) {
        return { text: 'Waiting for the server…', level: 'info' };
    }
    if (model.reduced) {
        return { text: 'Live, with some entities hidden while the server catches up.', level: 'warn' };
    }
    return { text: loginAvailable === false ? 'Live. Sign-in is not offered on this server.' : 'Live.', level: 'ok' };
}

export class PublicPage {
    /**
     * @param {HTMLElement} root The element the page fills.
     * @param {{api: object, socket: object, schedule: Function, observeSize?: Function}} deps `schedule(fn)` runs fn on the next frame.
     */
    constructor(root, { api, socket, schedule, observeSize }) {
        this.root = root;
        this.api = api;
        this.socket = socket;
        this.schedule = schedule;
        this.observeSize = observeSize ?? null;
        this.model = new MapModel();
        this.camera = new Camera();
        this.layerList = FALLBACK_LAYERS;
        this.layers = initialLayers(FALLBACK_LAYERS);
        this.maps = [];
        this.bookmarks = [];
        this.characters = [];
        this.selectedId = null;
        this.loginAvailable = null;
        this.socketState = 'connecting';
        this.fitted = false;
        this.width = 800;
        this.height = 600;
        this.dirty = false;
        this.drawn = 0;
    }

    // ---- build -----------------------------------------------------------------------------------------------------------------

    #build() {
        this.canvas = h('canvas', { class: 'pub-canvas', tabindex: 0, aria: { label: 'Live map' }, role: 'img' });
        this.ctx = this.canvas.getContext('2d');
        this.mapSelect = h('select', { class: 'input', id: 'pub-map', aria: { label: 'Map' }, on: { change: () => this.chooseMap(Number(this.mapSelect.value)) } });
        this.layerBox = h('div', { class: 'pub-layers', role: 'group', aria: { label: 'Layers' } });
        this.findInput = h('input', { class: 'input', id: 'pub-find', type: 'search', placeholder: 'Find by name', spellcheck: false, aria: { label: 'Find by name' }, attrs: { maxlength: 32 } });
        this.bookmarkSelect = h('select', { class: 'input', id: 'pub-bookmarks', aria: { label: 'Go to a saved place' }, on: { change: () => this.goToBookmark(this.bookmarkSelect.value) } });
        this.statusLine = h('span', { class: 'pub-status', role: 'status', aria: { live: 'polite' } });
        this.account = h('div', { class: 'pub-account' });
        this.counts = h('div', { class: 'pub-counts', aria: { live: 'off' } });
        this.results = h('ul', { class: 'pub-list', aria: { label: 'Search results' } });
        this.info = h('div', { class: 'pub-info', aria: { live: 'polite' } });
        this.characterList = h('ul', { class: 'pub-list', aria: { label: 'Your characters' } });
        this.charactersPanel = h('section', { class: 'pub-panel', hidden: true }, h('h2', { class: 'pub-h2', text: 'Your characters' }), this.characterList);
        this.loginHost = h('section', { class: 'pub-panel', hidden: true });
        this.message = h('div', { class: 'field-error', role: 'alert', hidden: true });

        const find = h('form', { class: 'pub-row', on: { submit: (e) => { e.preventDefault(); void this.find(this.findInput.value); } } },
            this.findInput, h('button', { class: 'button', type: 'submit', text: 'Find' }));
        fill(this.root, h('div', { class: 'pub' },
            h('header', { class: 'pub-bar' },
                h('h1', { class: 'pub-title', text: 'Atlas' }),
                this.mapSelect, this.layerBox, find, this.bookmarkSelect, this.statusLine, this.account),
            h('main', { class: 'pub-main' },
                h('div', { class: 'pub-stage' }, this.canvas),
                h('aside', { class: 'pub-side' },
                    this.message,
                    this.loginHost,
                    h('section', { class: 'pub-panel' }, h('h2', { class: 'pub-h2', text: 'On the map' }), this.counts, this.legend()),
                    h('section', { class: 'pub-panel' }, h('h2', { class: 'pub-h2', text: 'Selected' }), this.info),
                    h('section', { class: 'pub-panel' }, h('h2', { class: 'pub-h2', text: 'Found' }), this.results),
                    this.charactersPanel))));

        this.canvas.addEventListener('pointerdown', (e) => this.#pointerDown(e));
        this.canvas.addEventListener('pointermove', (e) => this.#pointerMove(e));
        this.canvas.addEventListener('pointerup', (e) => this.#pointerUp(e));
        this.canvas.addEventListener('wheel', (e) => this.#wheel(e), { passive: false });
        this.canvas.addEventListener('keydown', (e) => this.#key(e));
        this.observeSize?.(this.canvas.parentElement ?? this.canvas, (w, hgt) => this.resize(w, hgt));
        this.renderLayers();
        this.renderAccount();
        this.renderStatus();
    }

    legend() {
        const item = (swatch, text) => h('li', { class: 'pub-legend-item' }, h('span', { class: `pub-swatch ${swatch}`, aria: { hidden: true } }), text);
        return h('ul', { class: 'pub-legend', aria: { label: 'Legend' } },
            item('pub-swatch-creature', 'Creature (colour shows what it is doing)'),
            item('pub-swatch-object', 'Object'),
            item('pub-swatch-character', 'Character'),
            item('pub-swatch-cell', 'Several players are in this area'));
    }

    // ---- start -----------------------------------------------------------------------------------------------------------------

    async start() {
        this.#build();
        try {
            const status = await this.api.status();
            await this.api.me();
            this.loginAvailable = status?.login?.available ?? null;
        } catch (error) {
            this.#problem(error instanceof PublicApiError && error.code === 'unreachable' ? 'This page could not reach the server.' : 'The server did not accept this visit. Reload the page to try again.');
            return;
        }
        this.renderAccount();
        const [layers, bookmarks] = await Promise.allSettled([this.api.layers(), this.api.bookmarks()]);
        if (layers.status === 'fulfilled' && Array.isArray(layers.value) && layers.value.length > 0) {
            this.layerList = layers.value;
            this.layers = initialLayers(layers.value);
            this.renderLayers();
        }
        if (bookmarks.status === 'fulfilled' && Array.isArray(bookmarks.value)) {
            this.bookmarks = bookmarks.value.filter((b) => b && typeof b.name === 'string' && Array.isArray(b.at));
            this.renderBookmarks();
        }
        this.#wireSocket();
        this.socket.connect();
        if (this.api.tier === 'account') {
            void this.loadCharacters();
        }
    }

    #wireSocket() {
        this.socket.on('state', (state) => {
            this.socketState = state;
            this.renderStatus();
        });
        this.socket.on('hello', (hello) => {
            this.maps = (Array.isArray(hello.maps) ? hello.maps : []).filter((m) => Number.isInteger(m?.id) && typeof m.name === 'string');
            this.renderMaps();
            const first = this.maps[0]?.id ?? null;
            if (this.model.map === null || !this.maps.some((m) => m.id === this.model.map)) {
                this.model.reset(first);
                this.fitted = false;
            }
            if (this.model.map !== null) {
                this.mapSelect.value = String(this.model.map);
            }
            this.renderStatus();
        });
        this.socket.on('status', (data) => {
            this.model.applyStatus(data);
            this.renderStatus();
            this.invalidate();
        });
        this.socket.on('frame', (data) => {
            if (!this.model.applyFrame(data)) {
                return;
            }
            if (!this.fitted && this.model.entities.size > 0) {
                this.camera.fit([...this.model.entities.values()].map((e) => [e.x, e.y]), this.width, this.height);
                this.fitted = true;
            }
            this.renderCounts();
            this.renderStatus();
            this.invalidate();
        });
        this.socket.on('error', (error) => this.#problem(error.message || 'The server refused that.'));
        this.socket.on('expired', () => {
            void this.#renewVisit();
        });
    }

    async #renewVisit() {
        try {
            await this.api.status();
            await this.api.me();
            this.renderAccount();
            this.socket.connect();
        } catch {
            this.#problem('Your visit expired and could not be renewed. Reload the page.');
        }
    }

    // ---- rendering -------------------------------------------------------------------------------------------------------------

    #problem(text) {
        this.message.textContent = text;
        this.message.hidden = false;
    }

    renderStatus() {
        const { text, level } = statusText({ socketState: this.socketState, model: this.model, loginAvailable: this.loginAvailable });
        this.statusLine.textContent = text;
        this.statusLine.dataset.level = level;
    }

    renderMaps() {
        fill(this.mapSelect, this.maps.map((m) => h('option', { value: String(m.id), text: m.name })));
        if (this.model.map !== null) {
            this.mapSelect.value = String(this.model.map);
        }
    }

    renderLayers() {
        fill(this.layerBox, this.layerList.map((layer) => {
            const box = h('input', {
                type: 'checkbox',
                checked: this.layers.has(layer.id),
                id: `pub-layer-${layer.id}`,
                on: { change: () => this.setLayer(layer.id, box.checked) },
            });
            return h('label', { class: 'pub-layer', for: `pub-layer-${layer.id}` }, box, h('span', { text: String(layer.title ?? layer.id) }));
        }));
    }

    renderBookmarks() {
        fill(this.bookmarkSelect,
            h('option', { value: '', text: 'Go to…' }),
            this.bookmarks.map((b, i) => h('option', { value: String(i), text: b.name })));
    }

    renderCounts() {
        const c = this.model.counts(this.layers);
        const parts = [`${c.creature} creatures`, `${c.gameobject} objects`, `${c.character} characters`];
        if (c.cells > 0) {
            parts.push(`${c.cells} busy ${c.cells === 1 ? 'area' : 'areas'}`);
        }
        this.counts.textContent = parts.join(', ');
    }

    renderAccount() {
        if (this.api.tier === 'account') {
            const level = SECURITY_NAMES[this.api.security] ?? 'Account';
            fill(this.account,
                h('span', { class: 'pub-who', text: `${this.api.account} · ${level}` }),
                h('button', { class: 'button', type: 'button', text: 'Sign out', on: { click: () => void this.signOut() } }));
        } else if (this.loginAvailable === false) {
            fill(this.account, h('span', { class: 'pub-who', text: 'Guest' }));
        } else {
            fill(this.account,
                h('span', { class: 'pub-who', text: 'Guest' }),
                h('button', { class: 'button', type: 'button', text: 'Sign in', on: { click: () => this.openLogin() } }));
        }
    }

    renderInfo(rows, title) {
        fill(this.info,
            title ? h('div', { class: 'pub-info-title', text: title }) : null,
            h('dl', { class: 'pub-dl' }, rows.filter(([, v]) => v !== null && v !== undefined && v !== '').flatMap(([k, v]) => [h('dt', { text: k }), h('dd', { text: String(v) })])));
    }

    renderResults(entities) {
        fill(this.results, entities.length === 0
            ? h('li', { class: 'pub-empty', text: 'Nothing found.' })
            : entities.slice(0, 20).map((e) => h('li', {}, h('button', {
                class: 'button pub-link',
                type: 'button',
                text: `${e.name ?? 'Unnamed'} (${kindTitle[e.kind] ?? 'Entity'})`,
                on: { click: () => this.centerOn(e) },
            }))));
    }

    renderCharacters() {
        this.charactersPanel.hidden = this.api.tier !== 'account';
        fill(this.characterList, this.characters.length === 0
            ? h('li', { class: 'pub-empty', text: 'None of your characters is online.' })
            : this.characters.map((c) => h('li', {}, h('button', {
                class: 'button pub-link',
                type: 'button',
                text: `${c.n} · level ${c.level ?? '?'}`,
                on: { click: () => this.goToCharacter(c) },
            }))));
    }

    // ---- actions ---------------------------------------------------------------------------------------------------------------

    setLayer(id, visible) {
        if (visible) {
            this.layers.add(id);
        } else {
            this.layers.delete(id);
        }
        this.renderCounts();
        this.invalidate();
    }

    chooseMap(id) {
        if (!this.maps.some((m) => m.id === id)) {
            return;
        }
        this.model.reset(id);
        this.fitted = false;
        this.selectedId = null;
        this.mapSelect.value = String(id);
        this.socket.view(id);
        this.renderStatus();
        this.invalidate();
    }

    goToBookmark(index) {
        const bookmark = this.bookmarks[Number(index)];
        if (!bookmark) {
            return;
        }
        if (Number.isInteger(bookmark.map) && bookmark.map !== this.model.map) {
            this.chooseMap(bookmark.map);
        }
        this.camera.centerOn(bookmark.at[0], bookmark.at[1]);
        this.camera.scale = Math.min(Math.max(500 / Math.max(Number(bookmark.distance) || 300, 50), 0.05), 4);
        this.fitted = true;
        this.invalidate();
    }

    goToCharacter(character) {
        if (Number.isInteger(character.map) && character.map !== this.model.map && this.maps.some((m) => m.id === character.map)) {
            this.chooseMap(character.map);
        }
        this.camera.centerOn(character.x, character.y);
        this.camera.scale = 0.6;
        this.fitted = true;
        this.invalidate();
    }

    centerOn(entity) {
        const at = entity.at;
        if (Array.isArray(at)) {
            this.camera.centerOn(at[0], at[1]);
            this.camera.scale = Math.max(this.camera.scale, 0.6);
        }
        this.selectedId = typeof entity.id === 'string' ? entity.id : null;
        this.invalidate();
    }

    /** Runs a command over the socket when it is open, else over HTTP. */
    run(name, args) {
        return this.socket.ready ? this.socket.command(name, args) : this.api.command(name, args);
    }

    async find(text) {
        const name = String(text ?? '').trim();
        if (!FIND_PATTERN.test(name)) {
            this.#problem("Search with letters, digits, spaces, ' and - (up to 32 characters).");
            return;
        }
        this.message.hidden = true;
        try {
            const data = await this.run('live.find', { name, map: this.model.map ?? undefined });
            this.renderResults(Array.isArray(data?.entities) ? data.entities : []);
        } catch (error) {
            this.#problem(error?.code === 'rate-limited' ? 'Too many searches. Wait a moment.' : 'The search did not work. Try again.');
        }
    }

    async pickAt(sx, sy) {
        const hit = hitTest(this.model, this.camera, this.layers, sx, sy, this.width, this.height);
        if (!hit) {
            this.selectedId = null;
            fill(this.info);
            this.invalidate();
            return;
        }
        this.message.hidden = true;
        if (hit.type === 'cell') {
            this.selectedId = null;
            this.renderInfo([['Players', `${hit.cell.n} in this area`], ['Note', 'A coarse, delayed count; no names or exact positions.']], 'Busy area');
            this.invalidate();
            return;
        }
        this.selectedId = hit.entity.id;
        this.invalidate();
        const entity = hit.entity;
        this.renderInfo([['Name', entity.name || 'Unnamed'], ['Type', kindTitle[entity.kind]]], entity.name || kindTitle[entity.kind]);
        try {
            const data = await this.run('pick.public', { id: entity.id, map: this.model.map ?? undefined });
            this.renderInfo(
                [
                    ['Name', data.name ?? entity.name],
                    ['Type', data.type ?? kindTitle[entity.kind]],
                    ['Level', data.level],
                    ['Race', data.race],
                    ['Class', data.class],
                    ['Family', data.family],
                    ['Faction', data.faction],
                    ['Health', typeof data.hp === 'number' ? `${Math.round(data.hp)}%` : null],
                    ['Doing', data.motion],
                ],
                data.name ?? entity.name ?? kindTitle[entity.kind]);
        } catch (error) {
            if (error?.code === 'rate-limited') {
                this.#problem('Too many clicks. Wait a moment.');
            }
        }
    }

    openLogin() {
        this.loginForm ??= new LoginForm({
            onSubmit: (input) => this.signIn(input),
            onCancel: () => this.closeLogin(),
        });
        fill(this.loginHost, h('h2', { class: 'pub-h2', text: 'Sign in' }), this.loginForm.element);
        this.loginHost.hidden = false;
        this.loginForm.focus();
    }

    closeLogin() {
        this.loginForm?.clear();
        this.loginHost.hidden = true;
    }

    /** Signs in; a failure rejects with the PublicApiError so the form can show its message. */
    async signIn({ user, password, code }) {
        await this.api.login(user, password, code);
        this.closeLogin();
        this.renderAccount();
        this.characters = [];
        this.renderCharacters();
        void this.loadCharacters();
    }

    async signOut() {
        try {
            await this.api.logout();
        } catch {
            this.#problem('Signing out did not work. Reload the page.');
            return;
        }
        this.characters = [];
        this.renderCharacters();
        this.renderAccount();
    }

    async loadCharacters() {
        try {
            const data = await this.api.characters();
            this.characters = Array.isArray(data?.characters) ? data.characters : [];
        } catch {
            this.characters = [];
        }
        this.renderCharacters();
    }

    // ---- canvas ----------------------------------------------------------------------------------------------------------------

    resize(width, height) {
        if (!(width > 0 && height > 0)) {
            return;
        }
        this.width = width;
        this.height = height;
        const ratio = globalThis.devicePixelRatio || 1;
        this.canvas.width = Math.round(width * ratio);
        this.canvas.height = Math.round(height * ratio);
        this.ctx.setTransform?.(ratio, 0, 0, ratio, 0, 0);
        this.invalidate();
    }

    invalidate() {
        if (this.dirty) {
            return;
        }
        this.dirty = true;
        this.schedule(() => {
            this.dirty = false;
            this.drawn = drawMap(this.ctx, this.model, this.camera, this.layers, this.layerList, this.width, this.height, this.selectedId);
        });
    }

    #local(event) {
        const rect = this.canvas.getBoundingClientRect?.() ?? { left: 0, top: 0 };
        return [event.clientX - rect.left, event.clientY - rect.top];
    }

    #pointerDown(event) {
        this.drag = { x: event.clientX, y: event.clientY, moved: false };
        this.canvas.setPointerCapture?.(event.pointerId);
    }

    #pointerMove(event) {
        if (!this.drag) {
            return;
        }
        const dx = event.clientX - this.drag.x;
        const dy = event.clientY - this.drag.y;
        if (!this.drag.moved && Math.hypot(dx, dy) < DRAG_THRESHOLD) {
            return;
        }
        this.drag.moved = true;
        this.camera.panByPixels(dx, dy);
        this.drag.x = event.clientX;
        this.drag.y = event.clientY;
        this.fitted = true;
        this.invalidate();
    }

    #pointerUp(event) {
        const drag = this.drag;
        this.drag = null;
        if (drag && !drag.moved) {
            const [sx, sy] = this.#local(event);
            void this.pickAt(sx, sy);
        }
    }

    #wheel(event) {
        event.preventDefault();
        const [sx, sy] = this.#local(event);
        this.camera.zoomAt(sx, sy, event.deltaY < 0 ? 1.2 : 1 / 1.2, this.width, this.height);
        this.fitted = true;
        this.invalidate();
    }

    #key(event) {
        const step = 60;
        const moves = { ArrowUp: [0, step], ArrowDown: [0, -step], ArrowLeft: [step, 0], ArrowRight: [-step, 0] };
        if (moves[event.key]) {
            event.preventDefault();
            this.camera.panByPixels(...moves[event.key]);
            this.fitted = true;
            this.invalidate();
        } else if (event.key === '+' || event.key === '=' || event.key === '-') {
            event.preventDefault();
            this.camera.zoomAt(this.width / 2, this.height / 2, event.key === '-' ? 1 / 1.2 : 1.2, this.width, this.height);
            this.fitted = true;
            this.invalidate();
        }
    }
}
