// The action log panel: every action this Atlas ran, newest first, with who ran it, the line, the outcome, the daemon's reply, the old and
// new values it wrote, and Undo where the server computed an inverse.

import { Emitter, fill, h } from '../util.js';

/** The log kept in the browser. Entries are the `action.log` / `devui.action` JSON; ids are unique and ascending. */
export class ActionLogModel extends Emitter {
    #entries = [];

    get entries() {
        return this.#entries;
    }

    /** Replaces the list (from `action.log`). */
    replace(list) {
        this.#entries = [...list].sort((a, b) => a.id - b.id);
        this.emit('change');
    }

    /** Adds or updates one entry; marks the entry it undid as undone. */
    ingest(entry) {
        const at = this.#entries.findIndex((e) => e.id === entry.id);
        if (at >= 0) {
            this.#entries[at] = entry;
        } else {
            this.#entries.push(entry);
            this.#entries.sort((a, b) => a.id - b.id);
        }
        if (typeof entry.undoOf === 'number') {
            const original = this.#entries.find((e) => e.id === entry.undoOf);
            if (original) {
                original.undoneBy = entry.id;
                original.undoable = false;
            }
        }
        this.emit('change');
    }

    /** The newest entry Undo is offered for, or null. */
    lastUndoable() {
        for (let i = this.#entries.length - 1; i >= 0; i--) {
            if (this.#entries[i].undoable === true) {
                return this.#entries[i];
            }
        }
        return null;
    }
}

const clock = (iso) => {
    const d = new Date(iso);
    return Number.isNaN(d.getTime()) ? '' : d.toLocaleTimeString([], { hour12: false });
};

/** The panel. `onUndo(entry)` runs the undo; `onClose` hides it. */
export class ActionLogPanel {
    constructor({ model, onUndo, onClose }) {
        this.model = model;
        this.list = h('ol', { class: 'devui-log-list', aria: { label: 'Actions, newest first' } });
        this.empty = h('p', { class: 'muted devui-log-empty', text: 'No actions yet. Right-click the map, or press Ctrl+Shift+P.' });
        this.element = h('section', { class: 'devui-log', hidden: true, aria: { label: 'Action log' } },
            h('header', { class: 'devui-log-head' },
                h('h2', { class: 'devui-log-title', text: 'Action log' }),
                h('button', { class: 'icon-button', type: 'button', title: 'Close (Alt+L)', aria: { label: 'Close the action log' }, text: '×', on: { click: () => onClose() } })),
            this.empty, this.list);
        this.onUndo = onUndo;
        model.on('change', () => this.render());
        this.render();
    }

    get visible() {
        return !this.element.hidden;
    }

    toggle(force) {
        this.element.hidden = force === undefined ? !this.element.hidden : !force;
        this.render();
    }

    render() {
        const entries = [...this.model.entries].reverse();
        this.empty.hidden = entries.length > 0;
        fill(this.list, ...entries.map((entry) => this.#row(entry)));
    }

    #row(entry) {
        const writes = Array.isArray(entry.writes) ? entry.writes : [];
        const undo = entry.undoable
            ? h('button', { class: 'button devui-log-undo', type: 'button', text: 'Undo', title: 'Run the inverse of this action', on: { click: () => this.onUndo(entry) } })
            : null;
        const note = entry.undoneBy ? `undone by #${entry.undoneBy}` : entry.noUndoReason ? `no undo: ${entry.noUndoReason}` : '';
        const replies = Array.isArray(entry.replies) ? entry.replies.filter((r) => r !== '') : [];
        return h('li', { class: `devui-log-row ${entry.ok ? 'is-ok' : 'is-fail'}`, data: { id: entry.id } },
            h('div', { class: 'devui-log-line' },
                h('span', { class: 'devui-log-id num', text: `#${entry.id}` }),
                h('span', { class: 'devui-log-time num', text: clock(entry.at) }),
                h('span', { class: `result-status ${entry.ok ? 'is-ok' : 'is-fail'}`, text: entry.ok ? 'ok' : (entry.code ?? 'failed') }),
                undo),
            h('div', { class: 'devui-log-cmd num', text: (entry.lines ?? []).join('  ;  ') }),
            h('div', { class: 'devui-log-actor muted', text: entry.actor ?? '' }),
            entry.message ? h('div', { class: 'devui-log-msg', text: entry.message }) : null,
            replies.length > 0 ? h('pre', { class: 'devui-log-replies num', text: replies.join('\n') }) : null,
            writes.length > 0 ? h('div', { class: 'devui-log-writes num', text: writes.map((w) => `${w.field ?? w.what ?? 'value'}: ${w.old ?? '?'} -> ${w.new ?? '?'}`).join('; ') }) : null,
            note ? h('div', { class: 'devui-log-note muted', text: note }) : null);
    }
}
