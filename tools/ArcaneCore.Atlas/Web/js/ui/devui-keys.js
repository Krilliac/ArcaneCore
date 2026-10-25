// The dev UI's keyboard map. `k`, `t` and `f` are taken by hotkeys.js and the movement keys by nav.js, so the dev UI uses Alt combinations and
// Ctrl+Shift+P; both of those modules ignore Alt, and neither knows Delete or Ctrl+Z. Escape is shared on purpose: hotkeys.js clears the
// selection, and the dev UI closes its own menu first (it stops the event) and clears its entity selection with it.

import { isTypingTarget } from '../nav.js';
import { hasOpenDialog } from './popover.js';

/**
 * The keys. `id` is the dev UI action (a catalog action id) or one of the UI's own: palette, menu, log, undo, clear, follow.
 * `cooperative` marks a key that hotkeys.js also handles; every other combination must be unique across both modules.
 */
export const DEVUI_KEYS = [
    { combo: 'Ctrl+Shift+P', id: 'palette', label: 'GM command palette', global: true },
    { combo: 'Shift+F10', id: 'menu', label: 'Context menu at the cursor' },
    { combo: 'ContextMenu', id: 'menu', label: 'Context menu at the cursor' },
    { combo: 'Alt+K', id: 'kill', label: 'Kill the selection' },
    { combo: 'Alt+H', id: 'heal', label: 'Heal the selection' },
    { combo: 'Alt+T', id: 'tele.me', label: 'Teleport me to the cursor' },
    { combo: 'Alt+F', id: 'follow', label: 'Follow the selected entity' },
    { combo: 'Alt+L', id: 'log', label: 'Show or hide the action log', global: true },
    { combo: 'Delete', id: 'despawn', label: 'Despawn the selection (asks first)' },
    { combo: 'Ctrl+Z', id: 'undo', label: 'Undo the last action' },
    { combo: 'Escape', id: 'clear', label: 'Clear the entity selection', cooperative: true },
];

/** The canonical name of the key combination an event carries, for example `Ctrl+Shift+P` or `Alt+K`. */
export function comboOf(event) {
    const parts = [];
    if (event.ctrlKey || event.metaKey) {
        parts.push('Ctrl');
    }
    if (event.altKey) {
        parts.push('Alt');
    }
    if (event.shiftKey) {
        parts.push('Shift');
    }
    const key = event.key === ' ' ? 'Space' : event.key;
    if (key === 'Control' || key === 'Alt' || key === 'Shift' || key === 'Meta') {
        return parts.join('+');
    }
    parts.push(key.length === 1 ? key.toUpperCase() : key);
    return parts.join('+');
}

/** The key map entry for an event, or null. */
export function matchKey(event) {
    const combo = comboOf(event);
    return DEVUI_KEYS.find((k) => k.combo === combo) ?? null;
}

/** Normalises a hotkeys.js SHORTCUTS label (`Ctrl K`, `W A S D`, `1 to 8`, `[ ]`) into the single combinations it stands for. */
export function hotkeyCombos(label) {
    const text = String(label).trim();
    if (text === '1 to 8') {
        return [...'12345678'];
    }
    if (text === 'Esc') {
        return ['Escape'];
    }
    if (text === 'Shift' || text === 'Ctrl' || text === 'Alt') {
        return [];
    }
    const modified = /^(Ctrl|Alt|Shift) (\S)$/.exec(text);
    if (modified) {
        return [modified[1] + '+' + modified[2].toUpperCase()];
    }
    return text.split(' ').map((k) => (k.length === 1 ? k.toUpperCase() : k));
}

/**
 * Installs the key handler on the document.
 * @param {object} o
 * @param {(id: string, event: KeyboardEvent) => (boolean|void)} o.run  runs a key's action; return false when it did nothing (the event is left alone).
 * @returns {() => void} removes the listener
 */
export function installKeys({ run }) {
    const onKeyDown = (e) => {
        const key = matchKey(e);
        if (!key || e.repeat) {
            return;
        }
        const typing = isTypingTarget(e.target);
        if (!key.global && (typing || hasOpenDialog())) {
            return;
        }
        if (key.global && typing && key.combo === 'Alt+L') {
            return;
        }
        if (run(key.id, e) !== false) {
            e.preventDefault();
        }
    };
    document.addEventListener('keydown', onKeyDown);
    return () => document.removeEventListener('keydown', onKeyDown);
}
