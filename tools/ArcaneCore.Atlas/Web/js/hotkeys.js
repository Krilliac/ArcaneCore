// Global keyboard shortcuts. Movement keys (W A S D Q E) live in nav.js; everything else is here. Shortcuts are
// ignored while typing in a form control, and while a dialog is open (except Escape and Ctrl+K).

import { switchMode } from './camera.js';
import { GROUPS } from './catalog.js';
import { isTypingTarget } from './nav.js';
import { h } from './util.js';
import { openPalette } from './ui/palette.js';
import { closePopovers, closeTopDialog, hasOpenDialog, openDialog } from './ui/popover.js';

export const SHORTCUTS = [
    ['Ctrl K', 'Open the command palette'],
    ['/', 'Go to a coordinate'],
    ['T', 'Top-down view; press again to return'],
    ['M', 'Cycle terrain & model shading mode (Standard, Relief, Normals, Slope)'],
    ['K', 'Toggle topographic elevation contour lines'],
    ['F', 'Frame the followed entity, the selection, or what is loaded (Shift: always what is loaded)'],
    ['1 to 8', 'Show or hide a layer group: World, Terrain, Liquid, Collision, Navmesh, Reference, Differences, Live'],
    ['W A S D', 'Move the camera (in orbit and top views over the ground, in fly view along the look direction)'],
    ['Q E', 'Move the camera down and up'],
    ['Shift', 'Hold with a movement key to go four times faster'],
    ['P', 'Show or hide the performance HUD (frame rate, renderer and live feed numbers)'],
    ['[ ]', 'Hide or show the Layers rail and the right panel'],
    ['Esc', 'Close the open panel, then clear the selection'],
    ['?', 'Show this list'],
];

/** The layers a digit key toggles and which way: hide when any is on, else show the defaults (or all when none is a default). */
export function groupToggle(layers, view, groupId) {
    const members = layers.filter((l) => l.group === groupId && l.available !== false);
    if (members.length === 0) {
        return null;
    }
    const anyOn = members.some((l) => (view.layers[l.id]?.visible ?? l.defaultVisible) === true);
    if (anyOn) {
        return { ids: members.map((l) => l.id), visible: false };
    }
    const defaults = members.filter((l) => l.defaultVisible);
    return { ids: (defaults.length > 0 ? defaults : members).map((l) => l.id), visible: true };
}

/** Group id for a digit key ('1' is the first group), or null. */
export const groupForDigit = (digit) => GROUPS[Number(digit) - 1]?.id ?? null;

export function showShortcuts() {
    const list = h('dl', { class: 'shortcut-list' });
    for (const [keys, text] of SHORTCUTS) {
        list.append(h('dt', null, h('kbd', { class: 'kbd', text: keys })), h('dd', { text }));
    }
    const mouse = h('ul', { class: 'plain-list' },
        h('li', { text: 'Left-drag orbits (fly: looks around), right-drag pans, Shift + right-drag raises and lowers.' }),
        h('li', { text: 'Wheel zooms toward the cursor. Double-click centres the camera on the surface under it.' }),
        h('li', { text: 'A click asks the host what is under that point and fills the probe forms.' }));
    openDialog({ title: 'Keyboard and mouse', content: h('div', null, list, h('h3', { class: 'section-title', text: 'Mouse' }), mouse) });
}

/**
 * @param {object} o
 * @param {import('./app.js').App} o.app
 * @param {import('./viewport.js').Viewport} o.viewport
 * @param {import('./ui/topbar.js').TopBar} o.topbar
 * @param {(side: 'left'|'right') => void} o.toggleRail
 * @param {{ toggle: () => boolean } | undefined} [o.hud] the performance HUD (key P)
 * @returns {() => void} removes the key listener again
 */
export function installHotkeys({ app, viewport, topbar, toggleRail, hud }) {
    let returnMode = 'orbit';
    const onKeyDown = (e) => {
        const key = e.key;
        if ((e.ctrlKey || e.metaKey) && !e.altKey && key.toLowerCase() === 'k') {
            e.preventDefault();
            if (hasOpenDialog()) {
                closeTopDialog();
            } else {
                openPalette(app);
            }
            return;
        }
        if (key === 'Escape') {
            if (hasOpenDialog() || closePopovers()) {
                return;
            }
            if (isTypingTarget(e.target)) {
                viewport.root.focus({ preventScroll: true });
                return;
            }
            if (app.selection.point || app.selection.entityGuid) {
                app.select({});
                return;
            }
            if (document.activeElement instanceof HTMLElement && document.activeElement !== document.body) {
                viewport.root.focus({ preventScroll: true });
            }
            return;
        }
        if (e.ctrlKey || e.metaKey || e.altKey || hasOpenDialog() || isTypingTarget(e.target)) {
            return;
        }
        const lower = key.toLowerCase();
        if (key === '/') {
            e.preventDefault();
            topbar.focusGoto();
        } else if (key === '?') {
            e.preventDefault();
            showShortcuts();
        } else if (lower === 't') {
            e.preventDefault();
            const camera = app.view.camera;
            if (camera.mode === 'top') {
                app.setCamera(switchMode(camera, returnMode), { immediate: true });
            } else {
                returnMode = camera.mode;
                app.setCamera(switchMode(camera, 'top'), { immediate: true });
            }
        } else if (lower === 'm') {
            e.preventDefault();
            topbar.cycleShading?.();
        } else if (lower === 'k') {
            e.preventDefault();
            topbar.toggleContours?.();
        } else if (lower === 'f') {
            e.preventDefault();
            viewport.frameSelection({ all: e.shiftKey });
        } else if (lower === 'p' && hud) {
            e.preventDefault();
            hud.toggle();
        } else if (key === '[') {
            toggleRail('left');
        } else if (key === ']') {
            toggleRail('right');
        } else if (/^[1-8]$/.test(key)) {
            const groupId = groupForDigit(key);
            const toggle = groupId ? groupToggle(app.layers, app.view, groupId) : null;
            if (toggle) {
                e.preventDefault();
                app.setGroupVisible(toggle.ids, toggle.visible);
            }
        }
    };
    document.addEventListener('keydown', onKeyDown);
    return () => document.removeEventListener('keydown', onKeyDown);
}
