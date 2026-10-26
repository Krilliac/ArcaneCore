// Bottom status bar: tiles loaded, missing and rejected, triangle count, frame rate, the connection, and the last
// automation command that ran on the host (from the `command` event topic).

import { formatBytes, formatCount, formatDuration, h } from '../util.js';

const CONNECTION_TEXT = {
    connecting: 'Connecting to the host',
    open: 'Connected',
    closed: 'Disconnected, retrying',
    unauthorized: 'Token refused',
};

/** The text of the last-command cell: "view.set from ui:abc · ok · 3.2 ms". */
export function lastCommandText(last) {
    if (!last) {
        return 'No automation commands yet';
    }
    const origin = last.origin ? ` from ${last.origin}` : '';
    const time = Number.isFinite(last.elapsedMs) ? ` · ${formatDuration(last.elapsedMs)}` : '';
    return `${last.name}${origin} · ${last.ok ? 'ok' : `failed${last.message ? `: ${last.message}` : ''}`}${time}`;
}

export class StatusBar {
    constructor(app) {
        this.app = app;
        this.tiles = h('span', { class: 'status-item num' });
        this.triangles = h('span', { class: 'status-item num' });
        this.fps = h('span', { class: 'status-item num' });
        this.gpu = h('span', { class: 'status-item num' });
        this.dot = h('span', { class: 'dot dot-off', aria: { hidden: 'true' } });
        this.connection = h('span', { class: 'status-item' }, this.dot, h('span', { class: 'connection-text' }));
        this.name = h('input', {
            class: 'input session-name',
            type: 'text',
            spellcheck: false,
            autocomplete: 'off',
            title: 'The name other sessions see next to your marker',
            attrs: { maxlength: 32, 'aria-label': 'Session name' },
            on: { change: () => this.app.setSessionName(this.name.value) },
        });
        this.last = h('span', { class: 'status-item status-last num', title: 'The last command that ran on the host, from the UI, ctl, a script or MCP' });
        this.element = h('footer', { class: 'statusbar', role: 'contentinfo' },
            h('div', { class: 'status-group', role: 'status', aria: { label: 'Rendering status' } }, this.tiles, this.triangles, this.fps, this.gpu),
            h('div', { class: 'status-group' }, this.connection, this.name),
            h('div', { class: 'status-group status-grow', role: 'status', aria: { label: 'Last automation command', live: 'off' } }, this.last));
        app.events.on('stats', () => this.updateStats());
        app.events.on('connection', () => this.updateConnection());
        app.events.on('lastCommand', () => this.updateLast());
        app.events.on('connection', () => this.updateName());
        app.events.on('sessionName', () => this.updateName());
        this.updateStats();
        this.updateConnection();
        this.updateLast();
        this.updateName();
    }

    /** Shows the user's name (empty until one is set) with the host's default as the placeholder; never overwrites typing in progress. */
    updateName() {
        this.name.placeholder = this.app.defaultName || 'session name';
        if (document.activeElement !== this.name) {
            this.name.value = this.app.sessionName;
        }
    }

    updateStats() {
        const { stats } = this.app;
        const t = stats.tiles;
        const parts = [`${t.loaded} loaded`, `${t.missing} missing`, `${t.rejected} rejected`];
        if (t.errors > 0) {
            parts.push(`${t.errors} failed`);
        }
        if (t.loading > 0) {
            parts.push(`${t.loading} loading`);
        }
        this.tiles.textContent = `Tiles ${parts.join(' · ')}`;
        this.tiles.classList.toggle('is-error', t.rejected > 0 || t.errors > 0);
        this.triangles.textContent = `${formatCount(stats.triangles)} triangles`;
        this.fps.textContent = stats.fps === 0 ? 'idle' : `${stats.fps} fps`;
        this.gpu.textContent = stats.gpuBytes > 0 ? `${formatBytes(stats.gpuBytes)} on the GPU` : '';
        this.gpu.hidden = stats.gpuBytes === 0;
    }

    updateConnection() {
        const c = this.app.connection;
        this.dot.className = `dot ${c.state === 'open' ? 'dot-ok' : c.state === 'unauthorized' ? 'dot-error' : 'dot-warn'}`;
        this.connection.lastElementChild.textContent = `${CONNECTION_TEXT[c.state] ?? c.state}${c.version && c.state === 'open' ? ` · build ${String(c.version).slice(0, 8)}` : ''}`;
        this.connection.title = c.message || '';
    }

    updateLast() {
        this.last.textContent = lastCommandText(this.app.lastCommand);
        this.last.classList.toggle('is-error', this.app.lastCommand?.ok === false);
    }
}
