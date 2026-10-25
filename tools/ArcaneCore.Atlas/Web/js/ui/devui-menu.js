// The dev UI's context menu. The model (`buildMenu`) is pure: it turns the daemon's action catalog, what was right-clicked and the current
// selection into the items to show, so the menu is the server's truth and not a client list. `openMenu` draws it.

import { h } from '../util.js';

/** Menu sections in order, with their headings. */
export const GROUPS = [
    ['move', 'Move'],
    ['spawn', 'Spawn'],
    ['life', 'Life'],
    ['edit', 'Edit'],
    ['bot', 'Bots'],
    ['ai', 'AI'],
    ['debug', 'Debug'],
    ['view', 'View'],
];

const kindsAccept = (item, kind) => item.kinds.length === 0 || item.kinds.includes(kind);

/**
 * @param {object} o
 * @param {{items?: object[]}} o.catalog  the `action.catalog` data
 * @param {{type: 'point'|'entity', point: number[], entity?: {guid: string, kind: string, name?: string}}} o.target  what was right-clicked
 * @param {string[]} o.selection  the selected guids
 * @param {(guid: string) => (string|null)} o.kindOf  the live kind of a guid, or null when unknown
 * @param {(name: string) => boolean} o.hasCommand  whether the Atlas host registers a command
 * @returns {object[]} items: `{key, label, group, disabled?, title?, shortcut?, run}`; `run.type` is `action` (an `action.at`/`action.on`
 *   call: actionId, targets, point, args, argSpecs, pick, bulk) or `client` (`name` and its own fields)
 */
export function buildMenu({ catalog, target, selection, kindOf, hasCommand }) {
    const items = [];
    const actions = Array.isArray(catalog?.items) ? catalog.items : [];
    const point = target.point;
    const entity = target.entity ?? null;
    const selected = selection.filter((g) => kindOf(g) !== null);

    if (target.type === 'point') {
        for (const item of actions.filter((a) => a.scope === 'point')) {
            let targets = [];
            let disabled = false;
            let title = '';
            if (item.subject === 'selection') {
                targets = selected.filter((g) => kindsAccept(item, kindOf(g)));
                disabled = targets.length === 0;
                title = disabled ? `Select ${item.kinds.join(' or ')} first (click one, or Alt+drag a box).` : `${targets.length} selected`;
            }
            items.push({
                key: `action:${item.id}`,
                label: item.label,
                group: item.group,
                disabled,
                title,
                shortcut: item.shortcut ?? '',
                run: { type: 'action', actionId: item.id, targets, point, args: {}, argSpecs: item.args, pick: null, bulk: targets.length > 1 },
            });
        }
        if (hasCommand('live.query')) {
            items.push({ key: 'client:height', label: 'Height here', group: 'view', run: { type: 'client', name: 'query', kind: 'height', point } });
            const from = selected[0] ?? null;
            items.push({
                key: 'client:los', label: 'Line of sight from selection', group: 'view', disabled: from === null,
                title: from === null ? 'Select an entity first.' : '', run: { type: 'client', name: 'query', kind: 'los', point, from },
            });
            items.push({
                key: 'client:path', label: 'Path from selection to here', group: 'view', disabled: from === null,
                title: from === null ? 'Select an entity first.' : '', run: { type: 'client', name: 'query', kind: 'path', point, from },
            });
        }
        if (hasCommand('camera.goto') && hasCommand('bookmark.add')) {
            items.push({ key: 'client:bookmark', label: 'Bookmark here...', group: 'view', run: { type: 'client', name: 'bookmark', point } });
        }
        items.push({ key: 'client:copy', label: 'Copy coordinates', group: 'view', run: { type: 'client', name: 'copy', text: point.map((n) => Number(n).toFixed(2)).join(', ') } });
        return items;
    }

    // An entity. A right-click on a selected entity acts on the whole selection of the kinds an item accepts.
    const inSelection = selected.includes(entity.guid);
    for (const item of actions.filter((a) => a.scope === 'entity')) {
        if (item.subject === 'selection-bots') {
            const bots = selected.filter((g) => kindOf(g) === 'bot' && g !== entity.guid);
            if (bots.length === 0) {
                continue;
            }
            items.push({
                key: `action:${item.id}`,
                label: bots.length > 1 ? `${item.label} (${bots.length} bots)` : item.label,
                group: item.group,
                shortcut: item.shortcut ?? '',
                run: { type: 'action', actionId: item.id, targets: bots, point: null, args: { unit: entity.guid }, argSpecs: item.args.filter((a) => a.name !== 'unit'), pick: null, bulk: bots.length > 1 },
            });
            continue;
        }
        if (!kindsAccept(item, entity.kind)) {
            continue;
        }
        const targets = inSelection && selected.length > 1 ? selected.filter((g) => kindsAccept(item, kindOf(g))) : [entity.guid];
        items.push({
            key: `action:${item.id}`,
            label: targets.length > 1 ? `${item.label} (${targets.length})` : item.label,
            group: item.group,
            shortcut: item.shortcut ?? '',
            run: { type: 'action', actionId: item.id, targets, point: item.pick ? null : (item.needsPoint ? point : null), args: {}, argSpecs: item.args, pick: item.pick ?? null, bulk: targets.length > 1 },
        });
    }
    if (hasCommand('live.inspect')) {
        items.push({ key: 'client:inspect', label: 'Inspect', group: 'view', run: { type: 'client', name: 'inspect', guid: entity.guid } });
    }
    items.push({ key: 'client:select', label: inSelection ? 'Deselect' : 'Select', group: 'view', run: { type: 'client', name: 'select', guid: entity.guid, on: !inSelection } });
    if (hasCommand('live.follow')) {
        items.push({ key: 'client:follow', label: 'Follow camera', group: 'view', shortcut: 'Alt+F', run: { type: 'client', name: 'follow', guid: entity.guid } });
    }
    items.push({ key: 'client:copyguid', label: 'Copy guid', group: 'view', run: { type: 'client', name: 'copy', text: entity.guid } });
    return items;
}

/** The items grouped for display: `[[heading, items[]]...]` in menu order; empty groups are left out. */
export function groupItems(items) {
    return GROUPS.map(([id, heading]) => [heading, items.filter((i) => i.group === id)]).filter(([, list]) => list.length > 0);
}

/**
 * Draws the menu at the viewport position (x, y). Escape (handled before hotkeys.js, which would otherwise clear the selection), a click
 * outside or a scroll closes it. Returns `{element, close}`.
 */
export function openMenu({ items, x, y, onPick, heading = '', emptyText = 'No actions are available here.' }) {
    const groups = groupItems(items);
    const menu = h('div', { class: 'devui-menu', role: 'menu', tabindex: -1, aria: { label: heading || 'Actions' } });
    const buttons = [];
    if (heading) {
        menu.append(h('div', { class: 'devui-menu-title', text: heading }));
    }
    groups.forEach(([title, list], index) => {
        if (index > 0) {
            menu.append(h('div', { class: 'devui-menu-sep', role: 'separator' }));
        }
        menu.append(h('div', { class: 'devui-menu-group', text: title, aria: { hidden: 'true' } }));
        for (const item of list) {
            const button = h('button', {
                class: `devui-menu-item${item.disabled ? ' is-disabled' : ''}`,
                type: 'button',
                role: 'menuitem',
                disabled: item.disabled === true,
                title: item.title ?? '',
                data: { key: item.key },
                on: {
                    click: () => {
                        close();
                        onPick(item);
                    },
                },
            }, h('span', { class: 'devui-menu-label', text: item.label }), item.shortcut ? h('kbd', { class: 'kbd', text: item.shortcut }) : null);
            buttons.push(button);
            menu.append(button);
        }
    });
    if (groups.length === 0) {
        menu.append(h('div', { class: 'devui-menu-empty', text: emptyText }));
    }
    let closed = false;
    const close = () => {
        if (closed) {
            return;
        }
        closed = true;
        document.removeEventListener('keydown', onKey, true);
        document.removeEventListener('pointerdown', onOutside, true);
        window.removeEventListener('blur', close);
        window.removeEventListener('wheel', close, true);
        menu.remove();
    };
    const enabled = () => buttons.filter((b) => !b.disabled);
    const onKey = (e) => {
        if (e.key === 'Escape') {
            e.preventDefault();
            e.stopPropagation();
            close();
        } else if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
            e.preventDefault();
            e.stopPropagation();
            const list = enabled();
            const at = list.indexOf(document.activeElement);
            list[(at + (e.key === 'ArrowDown' ? 1 : -1) + list.length) % list.length]?.focus();
        } else if (e.key === 'Tab') {
            e.preventDefault();
            e.stopPropagation();
        }
    };
    const onOutside = (e) => {
        if (!menu.contains(e.target)) {
            close();
        }
    };
    document.body.append(menu);
    const rect = menu.getBoundingClientRect();
    menu.style.left = `${Math.max(4, Math.min(x, window.innerWidth - rect.width - 4))}px`;
    menu.style.top = `${Math.max(4, Math.min(y, window.innerHeight - rect.height - 4))}px`;
    document.addEventListener('keydown', onKey, true);
    document.addEventListener('pointerdown', onOutside, true);
    window.addEventListener('blur', close);
    window.addEventListener('wheel', close, { capture: true, passive: true });
    (enabled()[0] ?? menu).focus({ preventScroll: true });
    return { element: menu, close };
}
