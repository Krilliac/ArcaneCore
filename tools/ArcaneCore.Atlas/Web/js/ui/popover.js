// Small shared widgets: toasts, modal dialogs with a focus trap, and popovers anchored to a button. Plain DOM, no
// dependencies; every control is reachable from the keyboard and Escape closes whatever is on top.

import { h } from '../util.js';

const FOCUSABLE = 'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';

/** Visible, enabled controls inside a container, in tab order. */
export function focusables(container) {
    return [...container.querySelectorAll(FOCUSABLE)].filter((el) => !el.hidden && el.offsetParent !== null);
}

/** Stack of transient messages in the corner of the page. */
export class Toasts {
    constructor(parent) {
        this.root = h('div', { class: 'toasts', role: 'status', aria: { live: 'polite', relevant: 'additions' } });
        parent.append(this.root);
    }

    show({ message, kind = 'info' }) {
        const toast = h('div', { class: `toast toast-${kind}` },
            h('span', { class: 'toast-text', text: message }),
            h('button', { class: 'toast-close', type: 'button', title: 'Dismiss', aria: { label: 'Dismiss message' }, text: '×', on: { click: () => remove() } }));
        const remove = () => {
            clearTimeout(timer);
            toast.remove();
        };
        const timer = setTimeout(remove, kind === 'error' ? 10_000 : 5_000);
        this.root.append(toast);
        while (this.root.children.length > 4) {
            this.root.firstElementChild.remove();
        }
    }
}

const openDialogs = [];
let dialogCounter = 0;

/** True while a modal dialog is open (the viewport ignores keystrokes then). */
export const hasOpenDialog = () => openDialogs.length > 0;

/**
 * Opens a modal dialog with `title` and `content` (a node). Focus moves into it and returns to the opener when it closes;
 * Tab cycles inside; Escape or a click on the backdrop closes. Returns {close, element}.
 */
export function openDialog({ title, content, wide = false, onClose, initialFocus }) {
    const opener = document.activeElement;
    const titleId = `dlg-${++dialogCounter}`;
    const dialog = h('div', { class: `dialog${wide ? ' dialog-wide' : ''}`, role: 'dialog', aria: { modal: 'true', labelledby: titleId } },
        h('div', { class: 'dialog-head' },
            h('h2', { class: 'dialog-title', id: titleId, text: title }),
            h('button', { class: 'icon-button', type: 'button', title: 'Close (Esc)', aria: { label: 'Close' }, text: '×', on: { click: () => close() } })),
        h('div', { class: 'dialog-body' }, content));
    const backdrop = h('div', { class: 'backdrop', on: { mousedown: (e) => e.target === backdrop && close() } }, dialog);
    let closed = false;
    function close() {
        if (closed) {
            return;
        }
        closed = true;
        openDialogs.splice(openDialogs.indexOf(entry), 1);
        backdrop.remove();
        document.removeEventListener('keydown', onKey, true);
        if (openDialogs.length === 0) {
            document.body.classList.remove('modal-open');
        }
        if (opener instanceof HTMLElement && opener.isConnected) {
            opener.focus({ preventScroll: true });
        }
        onClose?.();
    }
    function onKey(e) {
        if (openDialogs[openDialogs.length - 1] !== entry) {
            return;
        }
        if (e.key === 'Escape') {
            e.preventDefault();
            e.stopPropagation();
            close();
        } else if (e.key === 'Tab') {
            const items = focusables(dialog);
            if (items.length === 0) {
                e.preventDefault();
                return;
            }
            const first = items[0];
            const last = items[items.length - 1];
            if (e.shiftKey && (document.activeElement === first || !dialog.contains(document.activeElement))) {
                e.preventDefault();
                last.focus();
            } else if (!e.shiftKey && (document.activeElement === last || !dialog.contains(document.activeElement))) {
                e.preventDefault();
                first.focus();
            }
        }
    }
    const entry = { close };
    openDialogs.push(entry);
    document.body.classList.add('modal-open');
    document.addEventListener('keydown', onKey, true);
    document.body.append(backdrop);
    const target = (typeof initialFocus === 'function' ? initialFocus() : initialFocus) ?? focusables(dialog.querySelector('.dialog-body'))[0] ?? dialog;
    if (target === dialog) {
        dialog.tabIndex = -1;
    }
    target.focus();
    return { close, element: dialog };
}

/** Closes the topmost dialog; true when there was one. */
export function closeTopDialog() {
    const top = openDialogs[openDialogs.length - 1];
    if (top) {
        top.close();
        return true;
    }
    return false;
}

const openPopovers = new Set();

/** Closes every open popover; true when any was open. */
export function closePopovers() {
    const any = openPopovers.size > 0;
    for (const p of [...openPopovers]) {
        p.close();
    }
    return any;
}

/**
 * A panel that opens under `button` and closes on Escape, on an outside click, or when focus leaves it. The button
 * carries aria-expanded; `onOpen` runs just before it shows (refresh its content there).
 */
export function createPopover(button, panel, { onOpen, align = 'start' } = {}) {
    panel.hidden = true;
    panel.classList.add('popover', `popover-${align}`);
    button.setAttribute('aria-haspopup', 'true');
    button.setAttribute('aria-expanded', 'false');
    const api = {
        get isOpen() {
            return !panel.hidden;
        },
        open() {
            closePopovers();
            onOpen?.();
            panel.hidden = false;
            button.setAttribute('aria-expanded', 'true');
            openPopovers.add(api);
            const first = focusables(panel)[0];
            first?.focus();
        },
        close(restoreFocus = true) {
            if (panel.hidden) {
                return;
            }
            panel.hidden = true;
            button.setAttribute('aria-expanded', 'false');
            openPopovers.delete(api);
            if (restoreFocus && panel.contains(document.activeElement)) {
                button.focus();
            }
        },
        toggle() {
            if (panel.hidden) {
                api.open();
            } else {
                api.close();
            }
        },
    };
    button.addEventListener('click', () => api.toggle());
    panel.addEventListener('keydown', (e) => {
        if (e.key === 'Escape') {
            e.preventDefault();
            e.stopPropagation();
            api.close();
        }
    });
    document.addEventListener('mousedown', (e) => {
        if (!panel.hidden && !panel.contains(e.target) && !button.contains(e.target)) {
            api.close(false);
        }
    });
    panel.addEventListener('focusout', (e) => {
        if (!panel.hidden && e.relatedTarget instanceof Node && !panel.contains(e.relatedTarget) && !button.contains(e.relatedTarget)) {
            api.close(false);
        }
    });
    return api;
}
