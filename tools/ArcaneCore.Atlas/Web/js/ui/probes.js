// Right rail, Probes tab: forms for the query.* commands (server) and their ref.query.* twins (reference), the clicked
// points pre-filled, and each answer shown next to the other with the differences marked. Every command that returns
// an overlay keeps its latest overlay here, toggleable.

import { frameBounds } from '../camera.js';
import { acceptsArg, autoFill, fieldsFromSchema, filterArgs } from '../forms.js';
import { probeKinds } from '../normalize.js';
import { differences, flatten, overlaySummary, probeTitle } from '../results.js';
import { clear, fill, formatDuration, h } from '../util.js';
import { buildForm } from './formview.js';
import { button, emptyState } from './widgets.js';

/** Fields of a probe kind: the server schema's, then those only the reference schema has. */
export function unionFields(server, reference) {
    const commands = [server, reference].filter(Boolean);
    const seen = new Map();
    for (const command of commands) {
        for (const field of fieldsFromSchema(command.schema)) {
            const prior = seen.get(field.name);
            if (!prior) {
                seen.set(field.name, { ...field, only: command === server && reference ? 'server' : command === reference && server ? 'reference' : null });
            } else {
                prior.required = prior.required && field.required;
                prior.only = null;
                prior.description = prior.description || field.description;
            }
        }
    }
    return [...seen.values()].map((f) => ({
        ...f,
        description: f.only ? `${f.description}${f.description ? ' ' : ''}(${f.only} command only)` : f.description,
    }));
}

export class ProbesPanel {
    constructor(app, viewport) {
        this.app = app;
        this.viewport = viewport;
        this.cards = new Map();
        this.overlayBox = h('section', { class: 'panel-section overlay-section' });
        this.cardBox = h('div', { class: 'probe-cards' });
        this.element = h('div', { class: 'panel-body probes', role: 'region', aria: { label: 'Probes' } }, this.overlayBox, this.cardBox);

        app.events.on('commands', () => this.rebuild());
        app.events.on('overlays', () => this.#renderOverlays());
        app.overlays.on('change', () => this.#renderOverlays());
        app.events.on('clicks', () => this.#autoFill());
        app.events.on('view', (view, changed) => changed.has('map') && this.#autoFill());
        app.events.on('use-point', ({ slot, point }) => this.#usePoint(slot, point));
        this.rebuild();
    }

    rebuild() {
        const { app } = this;
        this.cards.clear();
        const kinds = probeKinds(app.commands);
        if (kinds.length === 0) {
            fill(this.cardBox, emptyState(
                app.commands.length === 0 ? 'Waiting for the host' : 'No probes on this host',
                app.commands.length === 0
                    ? 'The command list has not arrived yet.'
                    : 'The host registers no query.* commands. Start arcane-atlas serve with --data so the server probes load.',
            ));
        } else {
            fill(this.cardBox, ...kinds.map((k, i) => this.#buildCard(k, i === 0)));
        }
        this.#autoFill();
        this.#renderOverlays();
    }

    #pointSources() {
        const { app } = this;
        return [
            { label: 'Last click', title: 'The point you clicked most recently', get: () => app.clicks[0] ?? null },
            { label: 'Previous click', title: 'The point you clicked before that', get: () => app.clicks[1] ?? null },
            { label: 'Camera target', title: 'The point the camera is looking at', get: () => app.view.camera.target },
        ];
    }

    #buildCard({ kind, server, reference }, open) {
        const { app } = this;
        const fields = unionFields(server, reference);
        const card = { kind, server, reference, fields, results: {}, open, form: null };
        const body = h('div', { class: 'probe-body' });
        const results = h('div', { class: 'probe-results' });
        const run = (which) => this.#run(card, which);
        const form = buildForm({
            fields,
            pointSources: this.#pointSources(),
            submitLabel: server && reference ? 'Run both' : server ? 'Run on server' : 'Run on reference',
            onSubmit: () => run(server && reference ? 'both' : server ? 'server' : 'reference'),
            actions: server && reference ? [button('Server only', () => run('server')), button('Reference only', () => run('reference'))] : [],
        });
        card.form = form;
        card.resultsNode = results;
        body.append(form.element, results);
        const toggleId = `probe-${kind}`;
        const toggle = h('button', {
            class: 'card-toggle',
            type: 'button',
            aria: { expanded: open ? 'true' : 'false', controls: toggleId },
            on: {
                click: () => {
                    card.open = !card.open;
                    toggle.setAttribute('aria-expanded', card.open ? 'true' : 'false');
                    body.hidden = !card.open;
                    toggle.firstElementChild.textContent = card.open ? '▾' : '▸';
                },
            },
        }, h('span', { class: 'disclosure-glyph', text: open ? '▾' : '▸', aria: { hidden: 'true' } }), h('span', { class: 'card-title', text: probeTitle(kind) }),
        h('span', { class: 'card-tags' }, server ? h('span', { class: 'tag tag-server', text: 'server' }) : null, reference ? h('span', { class: 'tag tag-reference', text: 'reference' }) : null));
        body.id = toggleId;
        body.hidden = !open;
        const summary = (server ?? reference).summary;
        card.element = h('section', { class: 'probe-card' }, toggle, summary ? h('p', { class: 'card-summary', text: summary }) : null, body);
        this.cards.set(kind, card);
        return card.element;
    }

    #autoFill() {
        const { app } = this;
        for (const card of this.cards.values()) {
            const values = autoFill(card.fields, { clicks: app.clicks, map: app.view.map });
            for (const [name, text] of Object.entries(values)) {
                card.form.set(name, text, { auto: true });
            }
        }
    }

    #usePoint(slot, point) {
        for (const card of this.cards.values()) {
            const points = card.fields.filter((f) => f.kind === 'point3');
            const field = points[Math.min(slot, points.length - 1)];
            if (field) {
                card.form.set(field.name, point.map((v) => Number(v.toFixed(3))).join(', '));
            }
        }
        this.app.toast(slot === 0 ? 'Point A set in the probe forms.' : 'Point B set in the probe forms.', 'info');
        this.app.events.emit('focus-tab', 'probes');
    }

    async #run(card, which) {
        const args = card.form.parse();
        if (!args) {
            return;
        }
        card.form.setBusy(true);
        const jobs = [];
        if (which !== 'reference' && card.server) {
            jobs.push(this.#exec(card, 'server', card.server, args));
        }
        if (which !== 'server' && card.reference) {
            jobs.push(this.#exec(card, 'reference', card.reference, args));
        }
        try {
            await Promise.all(jobs);
        } finally {
            card.form.setBusy(false);
        }
    }

    async #exec(card, side, command, args) {
        const sent = filterArgs(command.schema, args);
        const dropped = Object.keys(args).filter((k) => !acceptsArg(command.schema, k));
        const started = performance.now();
        const result = await this.app.runCommand(command.name, sent, { quiet: true });
        card.results[side] = { command: command.name, result, args: sent, dropped, elapsedMs: Number.isFinite(result.elapsedMs) ? result.elapsedMs : performance.now() - started };
        this.#renderResults(card);
    }

    #renderResults(card) {
        const rowsBySide = {};
        for (const side of ['server', 'reference']) {
            const r = card.results[side];
            rowsBySide[side] = r?.result.ok ? flatten(r.result.data) : null;
        }
        const diff = rowsBySide.server && rowsBySide.reference ? differences(rowsBySide.server, rowsBySide.reference) : new Set();
        const columns = ['server', 'reference'].filter((s) => card.results[s]).map((side) => this.#column(card, side, rowsBySide[side], diff));
        card.resultsNode.className = `probe-results${columns.length === 2 ? ' is-paired' : ''}`;
        const note = columns.length === 2
            ? h('p', { class: 'muted', text: diff.size === 0 ? 'The server and reference answers agree on every shared value.' : `${diff.size} shared value${diff.size === 1 ? '' : 's'} differ (marked). Differences between the engines are not always bugs.` })
            : null;
        fill(card.resultsNode, ...columns, note);
    }

    #column(card, side, rows, diff) {
        const { app } = this;
        const r = card.results[side];
        const ok = r.result.ok;
        const head = h('div', { class: 'result-head' },
            h('span', { class: `tag tag-${side}`, text: side }),
            h('code', { class: 'result-command', text: r.command }),
            h('span', { class: `result-status ${ok ? 'is-ok' : 'is-fail'}`, text: ok ? 'ok' : 'failed' }),
            h('span', { class: 'result-time num', text: formatDuration(r.elapsedMs) }));
        const body = h('div', { class: 'result-body' });
        if (!ok) {
            body.append(h('p', { class: 'result-error', role: 'alert', text: `${r.result.error?.code ?? 'failed'}: ${r.result.error?.message ?? 'The command failed.'}` }));
        } else if (rows.length === 0) {
            body.append(h('p', { class: 'muted', text: 'The command returned no values.' }));
        } else {
            const table = h('dl', { class: 'kv kv-tree num' });
            for (const row of rows) {
                const different = diff.has(row.path);
                table.append(
                    h('dt', { class: `kv-key${different ? ' is-diff' : ''}`, style: { paddingLeft: `${row.depth * 12}px` }, text: row.key }),
                    h('dd', { class: different ? 'is-diff' : '', text: row.text ?? '' }));
            }
            body.append(table);
        }
        if (r.dropped.length > 0) {
            body.append(h('p', { class: 'muted', text: `Not sent (this command takes no ${r.dropped.join(', ')}).` }));
        }
        const entry = ok ? app.overlays.get(r.command) : null;
        if (entry) {
            const box = h('input', { type: 'checkbox', checked: entry.visible, on: { change: () => app.overlays.setVisible(r.command, box.checked) } });
            const frame = entry.geometry.bounds
                ? button('Frame', () => {
                    const b = entry.geometry.bounds;
                    app.setCamera(frameBounds(app.view.camera, b.min, b.max, this.viewport.cssWidth / this.viewport.cssHeight), { immediate: true });
                }, { title: 'Move the camera to show the whole overlay' })
                : null;
            body.append(h('div', { class: 'overlay-line' }, h('label', { class: 'check' }, box, h('span', { text: 'Show on map' })), h('span', { class: 'muted', text: overlaySummary(entry.counts) }), frame));
        }
        return h('div', { class: `result result-${side}` }, head, body);
    }

    #renderOverlays() {
        const { app } = this;
        const entries = app.overlays.entries();
        if (entries.length === 0) {
            fill(this.overlayBox, h('h3', { class: 'section-title', text: 'Overlays on the map' }), h('p', { class: 'muted', text: 'A probe that draws something (a ray, a path, a hit point) is listed here so you can hide it.' }));
            return;
        }
        const list = h('ul', { class: 'overlay-list' }, entries.map((e) => {
            const box = h('input', { type: 'checkbox', checked: e.visible, aria: { label: `Show ${e.name}` }, on: { change: () => app.overlays.setVisible(e.name, box.checked) } });
            return h('li', { class: 'overlay-row' },
                h('label', { class: 'check' }, box, h('code', { text: e.name })),
                h('span', { class: 'muted', text: overlaySummary(e.counts) }),
                h('button', { class: 'icon-button', type: 'button', text: '×', title: `Remove the ${e.name} overlay`, aria: { label: `Remove the ${e.name} overlay` }, on: { click: () => app.overlays.remove(e.name) } }));
        }));
        fill(this.overlayBox, 
            h('div', { class: 'section-head' }, h('h3', { class: 'section-title', text: 'Overlays on the map' }), button('Clear all', () => app.overlays.clear())),
            list);
    }
}
