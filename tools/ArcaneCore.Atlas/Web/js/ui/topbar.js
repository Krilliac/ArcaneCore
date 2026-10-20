// Top bar: map picker, coordinate go-to, bookmarks, camera mode, compare mode, capture, theme, HUD, palette.

import { switchMode } from '../camera.js';
import { LANDMARKS, parseGoto } from '../goto.js';
import { mapLabel } from '../normalize.js';
import { fill, formatPoint, h } from '../util.js';
import { cameraPatch } from '../view.js';
import { createPopover } from './popover.js';
import { button, copyText, segmented } from './widgets.js';

const CAMERA_OPTIONS = [
    { value: 'orbit', label: 'Orbit', title: 'Orbit: left-drag turns, right-drag pans, wheel zooms toward the cursor' },
    { value: 'fly', label: 'Fly', title: 'Fly: W A S D move, Q and E lower and raise, Shift is faster, left-drag looks around' },
    { value: 'top', label: 'Top', title: 'Top-down orthographic view (T toggles)' },
];

const COMPARE_OPTIONS = [
    { value: 'server', label: 'Server', title: 'Server layers: what the world server reads' },
    { value: 'reference', label: 'Reference', title: 'Reference layers: the 1.12.1 client data and DotRecast' },
    { value: 'split', label: 'Split', title: 'Server on the left, reference on the right, one camera' },
    { value: 'diff', label: 'Diff', title: 'Differences between server and reference over a dimmed server view' },
];

export const SHADING_OPTIONS = [
    { value: 0, label: 'Standard', title: 'Standard directional shading (M cycles)' },
    { value: 1, label: 'Relief', title: 'Elevation relief shading (M cycles)' },
    { value: 2, label: 'Normals', title: 'Surface normal debug shading (M cycles)' },
    { value: 3, label: 'Slope', title: 'Slope and walkability angle shading (M cycles)' },
];

export class TopBar {
    constructor(app, viewport, { onPalette, onShortcuts }) {
        this.app = app;
        this.viewport = viewport;
        this.shadingMode = 0;
        this.contours = false;

        this.mapSelect = h('select', { class: 'select', title: 'Map', aria: { label: 'Map' }, on: { change: () => this.#changeMap() } });
        this.landmarkList = h('datalist', { id: 'atlas-landmarks' },
            LANDMARKS.map((l) => h('option', { value: l.name, text: `Map ${l.map} (${l.x}, ${l.y}, ${l.z})` })));
        this.gotoInput = h('input', {
            class: 'input goto-input',
            type: 'text',
            placeholder: 'x, y, z   or   map x y z',
            spellcheck: false,
            autocomplete: 'off',
            attrs: { list: 'atlas-landmarks' },
            title: 'Go to a coordinate or landmark. Press / to focus. Enter goes; z is optional.',
            aria: { label: 'Go to coordinate', describedby: 'goto-message' },
            on: {
                keydown: (e) => {
                    if (e.key === 'Enter') {
                        e.preventDefault();
                        this.#goto();
                    } else if (e.key === 'Escape') {
                        this.gotoInput.blur();
                    }
                },
            },
        });
        this.gotoMessage = h('div', { class: 'goto-message', id: 'goto-message', role: 'status', hidden: true });
        this.gotoButton = button('Go', () => this.#goto(), { title: 'Go to the coordinate' });

        this.bookmarkButton = h('button', { class: 'button', type: 'button', text: 'Bookmarks' });
        this.bookmarkPanel = h('div', { class: 'bookmark-panel' });
        this.bookmarkPopover = createPopover(this.bookmarkButton, this.bookmarkPanel, { onOpen: () => this.#renderBookmarks() });

        this.camera = segmented({ label: 'Camera mode', options: CAMERA_OPTIONS, onSelect: (mode) => this.setCameraMode(mode) });
        this.compare = segmented({
            label: 'Compare mode',
            options: COMPARE_OPTIONS,
            onSelect: (compare) => this.app.patchView({ compare }, { immediate: true }),
        });

        this.shading = segmented({
            label: 'Shading mode',
            options: SHADING_OPTIONS,
            onSelect: (mode) => this.setShading(Number(mode), this.contours),
        });
        this.shading.update(this.shadingMode);

        this.contoursButton = h('button', {
            class: 'button',
            type: 'button',
            text: 'Contours',
            title: 'Toggle elevation contour lines (K)',
            aria: { pressed: 'false' },
            on: { click: () => this.toggleContours() },
        });

        this.captureButton = h('button', { class: 'button', type: 'button', text: 'Capture', title: 'Save the current view as a PNG' });
        this.capturePanel = h('div', { class: 'capture-panel' });
        this.capturePopover = createPopover(this.captureButton, this.capturePanel, { onOpen: () => this.#renderCapture(), align: 'end' });

        this.themeButton = h('button', {
            class: 'button',
            type: 'button',
            on: { click: () => this.app.patchView({ theme: this.app.view.theme === 'graphite' ? 'vellum' : 'graphite' }, { immediate: true }) },
        });
        this.hudButton = h('button', {
            class: 'button',
            type: 'button',
            text: 'HUD',
            title: 'Show or hide the crosshair, compass, scale bar and cursor readout',
            on: { click: () => this.app.patchView({ hud: this.app.view.hud === false }, { immediate: true }) },
        });
        this.paletteButton = h('button', { class: 'button', type: 'button', title: 'Command palette (Ctrl+K)', on: { click: () => onPalette() } },
            h('span', { text: 'Commands' }), h('kbd', { class: 'kbd', text: 'Ctrl K' }));
        this.helpButton = h('button', { class: 'button icon-button', type: 'button', text: '?', title: 'Keyboard shortcuts (?)', aria: { label: 'Keyboard shortcuts' }, on: { click: () => onShortcuts() } });

        this.element = h('header', { class: 'topbar', role: 'banner' },
            h('div', { class: 'brand', text: 'Atlas' }),
            h('div', { class: 'topbar-group' }, h('label', { class: 'sr-only', for: 'map-select', text: 'Map' }), this.mapSelect),
            h('div', { class: 'topbar-group goto' }, this.gotoInput, this.landmarkList, this.gotoButton, this.gotoMessage),
            h('div', { class: 'topbar-group popover-host' }, this.bookmarkButton, this.bookmarkPanel),
            h('div', { class: 'topbar-group' }, this.camera.element),
            h('div', { class: 'topbar-group' }, this.compare.element),
            h('div', { class: 'topbar-group tb-group-shading' }, this.shading.element, this.contoursButton),
            h('div', { class: 'topbar-spacer' }),
            h('div', { class: 'topbar-group popover-host' }, this.captureButton, this.capturePanel),
            h('div', { class: 'topbar-group' }, this.themeButton, this.hudButton, this.paletteButton, this.helpButton));
        this.mapSelect.id = 'map-select';

        const refresh = () => this.update();
        app.events.on('view', refresh);
        app.events.on('maps', refresh);
        app.events.on('bookmarks', () => this.bookmarkPopover.isOpen && this.#renderBookmarks());
        app.events.on('commands', refresh);
        this.update();
    }

    /** Sets terrain shading mode (0..3) and contour lines flag on the renderer and marks the viewport dirty. */
    setShading(mode = this.shadingMode, contours = this.contours) {
        this.shadingMode = ((Number(mode) || 0) % 4 + 4) % 4;
        this.contours = Boolean(contours);
        this.shading.update(this.shadingMode);
        this.contoursButton.setAttribute('aria-pressed', this.contours ? 'true' : 'false');
        this.contoursButton.classList.toggle('is-on', this.contours);
        const vp = this.app.viewport ?? this.viewport;
        vp?.renderer?.setShading?.({ mode: this.shadingMode, contours: this.contours });
        vp?.markDirty?.();
        vp?.requestRender?.();
    }

    cycleShading() {
        this.setShading((this.shadingMode + 1) % 4, this.contours);
    }

    toggleContours() {
        this.setShading(this.shadingMode, !this.contours);
    }

    /** Reflects the view, map list and command list in the controls. */
    update() {
        const { app } = this;
        const view = app.view;
        this.camera.update(view.camera.mode);
        this.compare.update(view.compare);
        this.shading.update(this.shadingMode);
        this.contoursButton.setAttribute('aria-pressed', this.contours ? 'true' : 'false');
        const map = view.map;
        const wanted = new Set([...app.maps.map((m) => m.id), map]);
        const key = [...wanted].sort((a, b) => a - b).join(',');
        if (this.mapSelect.dataset.key !== `${key}|${app.maps.length}`) {
            this.mapSelect.dataset.key = `${key}|${app.maps.length}`;
            fill(this.mapSelect, ...[...wanted].sort((a, b) => a - b).map((id) => {
                const info = app.maps.find((m) => m.id === id) ?? { id, name: '', tiles: {} };
                return h('option', { value: String(id), text: mapLabel(info) });
            }));
        }
        this.mapSelect.value = String(map);
        const dark = view.theme !== 'vellum';
        this.themeButton.textContent = dark ? 'Graphite' : 'Vellum';
        this.themeButton.title = `Theme: ${dark ? 'Graphite (dark)' : 'Vellum (light)'}. Click for ${dark ? 'Vellum (light)' : 'Graphite (dark)'}.`;
        this.themeButton.setAttribute('aria-label', this.themeButton.title);
        this.hudButton.setAttribute('aria-pressed', view.hud === false ? 'false' : 'true');
        const hasReference = app.layers.some((l) => l.group === 'Reference' && l.available !== false);
        this.compare.element.title = hasReference ? '' : 'No reference source is registered on this host; start it with --client to enable Reference, Split and Diff.';
    }

    /** Applies a camera mode, keeping the view where it is (top is the orbit camera seen from above). */
    setCameraMode(mode) {
        const camera = this.app.view.camera;
        if (camera.mode !== mode) {
            this.app.setCamera(switchMode(camera, mode), { immediate: true });
        }
    }

    focusGoto() {
        this.gotoInput.focus();
        this.gotoInput.select();
    }

    #changeMap() {
        const map = Number(this.mapSelect.value);
        if (Number.isInteger(map) && map !== this.app.view.map) {
            this.app.patchView({ map }, { immediate: true });
        }
    }

    #say(text, level = 'error') {
        this.gotoMessage.textContent = text;
        this.gotoMessage.hidden = text === '';
        this.gotoMessage.className = `goto-message goto-${level}`;
    }

    async #goto() {
        const { app } = this;
        const parsed = parseGoto(this.gotoInput.value, app.view.map);
        if (parsed.error) {
            this.#say(parsed.error);
            this.gotoInput.setAttribute('aria-invalid', 'true');
            this.gotoInput.focus();
            return;
        }
        this.gotoInput.setAttribute('aria-invalid', 'false');
        this.#say('');
        const camera = app.view.camera;
        if (parsed.z === null && app.hasCommand('camera.goto')) {
            const result = await app.runCommand('camera.goto', { map: parsed.map, x: parsed.x, y: parsed.y }, { quiet: true });
            if (!result.ok) {
                this.#say(result.error?.message ?? 'The host could not go there.');
            }
            return;
        }
        const target = [parsed.x, parsed.y, parsed.z ?? camera.target[2]];
        app.patchView({ map: parsed.map, ...cameraPatch({ ...camera, target }) }, { immediate: true });
        if (parsed.z === null) {
            this.#say('No z given, so the camera keeps its height. Click the surface to read the real z.', 'info');
        }
    }

    // ------------------------------------------------------------------------------------------------------------

    #renderBookmarks() {
        const { app } = this;
        const list = h('ul', { class: 'bookmark-list', role: 'list' });
        if (app.bookmarks.length === 0) {
            list.append(h('li', { class: 'empty-line', text: app.hasCommand('bookmarks.list') ? 'No bookmarks yet. Name this view below to add one.' : 'The host has no bookmark commands.' }));
        }
        for (const b of app.bookmarks) {
            list.append(h('li', { class: 'bookmark-row' },
                h('button', {
                    class: 'bookmark-go',
                    type: 'button',
                    title: b.at ? `${b.name}: map ${b.map ?? app.view.map}, ${formatPoint(b.at, 1)}${b.note ? `. ${b.note}` : ''}` : b.name,
                    on: { click: () => this.#goBookmark(b) },
                }, h('span', { class: 'bookmark-name', text: b.name }), b.at ? h('span', { class: 'bookmark-where num', text: formatPoint(b.at, 0) }) : null),
                b.builtIn ? null : h('button', {
                    class: 'icon-button',
                    type: 'button',
                    text: '×',
                    title: `Remove bookmark ${b.name}`,
                    aria: { label: `Remove bookmark ${b.name}` },
                    on: { click: () => this.#removeBookmark(b) },
                })));
        }
        const landmarkList = h('ul', { class: 'bookmark-list landmark-list', role: 'list' });
        for (const lm of LANDMARKS) {
            const at = [lm.x, lm.y, lm.z];
            landmarkList.append(h('li', { class: 'bookmark-row' },
                h('button', {
                    class: 'bookmark-go',
                    type: 'button',
                    title: `${lm.name}: map ${lm.map}, ${formatPoint(at, 1)}`,
                    on: {
                        click: () => {
                            this.bookmarkPopover.close();
                            const camera = app.view.camera;
                            app.patchView({ map: lm.map, ...cameraPatch({ ...camera, target: at }) }, { immediate: true });
                        },
                    },
                }, h('span', { class: 'bookmark-name', text: lm.name }), h('span', { class: 'bookmark-where num', text: `M${lm.map} · ${formatPoint(at, 0)}` }))));
        }
        const name = h('input', { class: 'input', type: 'text', placeholder: 'Name this view', spellcheck: false, aria: { label: 'Bookmark name' } });
        const add = async () => {
            const value = name.value.trim();
            if (value === '') {
                name.focus();
                return;
            }
            const result = await app.runCommand('bookmark.add', { name: value }, { quiet: true });
            if (result.ok) {
                name.value = '';
                await app.refreshBookmarks();
            } else {
                app.toast(`bookmark.add: ${result.error?.message ?? 'failed'}`, 'error');
            }
        };
        name.addEventListener('keydown', (e) => {
            if (e.key === 'Enter') {
                e.preventDefault();
                add();
            }
        });
        const form = app.hasCommand('bookmark.add')
            ? h('div', { class: 'bookmark-add' }, name, button('Add', add, { title: 'Save the current camera under this name' }))
            : null;
        fill(this.bookmarkPanel, list, form, h('h4', { class: 'section-title', text: 'Classic Landmarks' }), landmarkList);
    }

    async #goBookmark(b) {
        const { app } = this;
        this.bookmarkPopover.close();
        if (app.hasCommand('bookmark.go')) {
            const result = await app.runCommand('bookmark.go', { name: b.name }, { quiet: true });
            if (!result.ok) {
                app.toast(`bookmark.go: ${result.error?.message ?? 'failed'}`, 'error');
            }
        } else if (b.at) {
            const camera = app.view.camera;
            app.patchView({ map: b.map ?? app.view.map, ...cameraPatch({ ...camera, target: b.at }) }, { immediate: true });
        }
    }

    async #removeBookmark(b) {
        const result = await this.app.runCommand('bookmark.remove', { name: b.name }, { quiet: true });
        if (result.ok) {
            await this.app.refreshBookmarks();
        } else {
            this.app.toast(`bookmark.remove: ${result.error?.message ?? 'failed'}`, 'error');
        }
    }

    // ------------------------------------------------------------------------------------------------------------

    #renderCapture() {
        const { app, viewport } = this;
        const width = h('input', { class: 'input num', type: 'text', value: '1600', spellcheck: false, aria: { label: 'Width in pixels' }, attrs: { inputmode: 'numeric' } });
        const height = h('input', { class: 'input num', type: 'text', value: '900', spellcheck: false, aria: { label: 'Height in pixels' }, attrs: { inputmode: 'numeric' } });
        const message = h('div', { class: 'capture-message', role: 'status' });
        const size = () => {
            const w = Number(width.value);
            const hh = Number(height.value);
            if (!Number.isInteger(w) || !Number.isInteger(hh) || w < 64 || hh < 64 || w > 8192 || hh > 8192) {
                message.textContent = 'Width and height must be whole pixels from 64 to 8192.';
                return null;
            }
            return { width: w, height: hh };
        };
        const download = async (e) => {
            const dims = size();
            if (!dims) {
                return;
            }
            const target = e.currentTarget;
            target.disabled = true;
            message.textContent = 'Rendering.';
            try {
                const blob = await viewport.capturePng(dims);
                const tile = app.focusTile();
                const link = h('a', {
                    attrs: { download: `atlas-map${app.view.map}${tile ? `-${tile.x}-${tile.y}` : ''}-${dims.width}x${dims.height}.png` },
                });
                link.href = URL.createObjectURL(blob);
                link.click();
                setTimeout(() => URL.revokeObjectURL(link.href), 10_000);
                message.textContent = `Saved ${dims.width} × ${dims.height} PNG (${Math.round(blob.size / 1024)} KiB) to your downloads.`;
            } catch (error) {
                message.textContent = `Capture failed: ${error.message}`;
            } finally {
                target.disabled = false;
            }
        };
        const onHost = async (e) => {
            const dims = size();
            if (!dims) {
                return;
            }
            const target = e.currentTarget;
            target.disabled = true;
            message.textContent = 'Asking the host to capture.';
            const result = await app.runCommand('capture', { ...dims, source: 'ui' }, { quiet: true });
            target.disabled = false;
            if (result.ok) {
                const path = result.data?.path ?? '';
                fill(message, h('span', { text: `Saved ${path}` }), ' ', path ? button('Copy path', () => copyText(path)) : null);
            } else {
                message.textContent = `The host could not capture: ${result.error?.message ?? 'failed'}`;
            }
        };
        fill(this.capturePanel, 
            h('div', { class: 'capture-size' }, h('label', { class: 'field-label' }, h('span', { class: 'field-name', text: 'Width' }), width), h('span', { class: 'capture-times', text: '×' }),
                h('label', { class: 'field-label' }, h('span', { class: 'field-name', text: 'Height' }), height)),
            h('div', { class: 'capture-actions' },
                button('Download PNG', download, { primary: true, title: 'Render here and download the image' }),
                app.hasCommand('capture') ? button('Save on the host', onHost, { title: 'Run the capture command: the host writes the PNG under its captures folder' }) : null),
            message);
    }
}
