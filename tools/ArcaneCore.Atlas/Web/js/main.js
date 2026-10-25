// Entry point: moves the access token from the URL fragment into session storage, builds the layout and starts the app.

import { adoptToken, forgetToken, rememberToken } from './api.js';
import { createApp } from './app.js';
import { installHotkeys, showShortcuts } from './hotkeys.js';
import { installDevUi } from './ui/devui.js';
import { InsightPanel } from './insight/panel.js';
import { PerfHud } from './insight/perfhud.js';
import { Inspector } from './ui/inspector.js';
import { LayersRail } from './ui/layers.js';
import { LivePanel } from './ui/livepanel.js';
import { openPalette } from './ui/palette.js';
import { Toasts } from './ui/popover.js';
import { ProbesPanel } from './ui/probes.js';
import { StatusBar } from './ui/statusbar.js';
import { TopBar } from './ui/topbar.js';
import { Viewport } from './viewport.js';
import { fill, h } from './util.js';

const TOKEN_PATTERN = /^[A-Za-z0-9_-]{16,}$/;
const root = document.getElementById('app');

function showFailure(title, detail) {
    fill(root, h('main', { class: 'gate' }, h('div', { class: 'gate-card' }, h('h1', { class: 'gate-title', text: title }), h('p', { class: 'gate-text', text: detail }))));
}

/** The page has no token (or the host refused it): ask for one. */
function showGate(message) {
    const input = h('input', {
        class: 'input',
        type: 'password',
        id: 'token-input',
        autocomplete: 'off',
        spellcheck: false,
        aria: { describedby: 'token-help token-error' },
    });
    const error = h('div', { class: 'field-error', id: 'token-error', role: 'alert', hidden: !message, text: message ?? '' });
    const form = h('form', {
        class: 'gate-form',
        on: {
            submit: (e) => {
                e.preventDefault();
                const value = input.value.trim();
                if (!TOKEN_PATTERN.test(value)) {
                    error.textContent = 'That does not look like an Atlas token. It is a long run of letters, digits, dashes and underscores.';
                    error.hidden = false;
                    input.focus();
                    return;
                }
                rememberToken(window.sessionStorage, value);
                start(value);
            },
        },
    },
    h('label', { class: 'field-label', for: 'token-input' }, h('span', { class: 'field-name', text: 'Access token' })),
    input, error,
    h('button', { class: 'button button-primary', type: 'submit', text: 'Connect' }));
    fill(root, h('main', { class: 'gate' }, h('div', { class: 'gate-card' },
        h('h1', { class: 'gate-title', text: 'Atlas needs its access token' }),
        h('p', { class: 'gate-text', id: 'token-help', text: 'Open the link that "arcane-atlas serve" printed (or start it with --open) and this page gets the token by itself. You can also paste the token here.' }),
        form)));
    input.focus();
}

function railToggle(side, onClick) {
    return h('button', {
        class: `rail-toggle rail-toggle-${side}`,
        type: 'button',
        text: side === 'left' ? 'Layers' : 'Panels',
        title: side === 'left' ? 'Hide or show the Layers rail ([)' : 'Hide or show the right panel (])',
        aria: { pressed: 'true' },
        on: { click: onClick },
    });
}

const TABS = [
    { id: 'inspector', label: 'Inspector' },
    { id: 'probes', label: 'Probes' },
    { id: 'live', label: 'Live' },
    { id: 'insight', label: 'Insight' },
];

function start(token) {
    const app = createApp(token, window.location);
    const stage = h('main', { class: 'stage', tabindex: 0, aria: { label: 'Map viewport. Drag to orbit, right-drag to pan, wheel to zoom.' } });
    const toasts = new Toasts(document.body);
    app.events.on('toast', (t) => toasts.show(t));

    const viewport = new Viewport(app, stage);
    const layers = new LayersRail(app);
    const inspector = new Inspector(app, viewport);
    const probes = new ProbesPanel(app, viewport);
    const live = new LivePanel(app, viewport);
    const hud = new PerfHud(app, viewport);
    const insight = new InsightPanel(app, hud);
    const status = new StatusBar(app);
    const topbar = new TopBar(app, viewport, { onPalette: () => openPalette(app), onShortcuts: () => showShortcuts() });

    // Right rail with tabs.
    const panels = { inspector: inspector.element, probes: probes.element, live: live.element, insight: insight.element };
    const tabButtons = new Map();
    const tabList = h('div', { class: 'tabs', role: 'tablist', aria: { label: 'Panels' } });
    const select = (id, focus = false) => {
        for (const [tabId, button] of tabButtons) {
            const on = tabId === id;
            button.setAttribute('aria-selected', on ? 'true' : 'false');
            button.tabIndex = on ? 0 : -1;
            panels[tabId].hidden = !on;
            if (on && focus) {
                button.focus();
            }
        }
        live.setActive(id === 'live');
    };
    TABS.forEach((tab, i) => {
        const button = h('button', {
            class: 'tab',
            type: 'button',
            role: 'tab',
            id: `tab-${tab.id}`,
            text: tab.label,
            aria: { selected: 'false', controls: `panel-${tab.id}` },
            on: {
                click: () => select(tab.id),
                keydown: (e) => {
                    const step = e.key === 'ArrowRight' ? 1 : e.key === 'ArrowLeft' ? -1 : 0;
                    if (step !== 0) {
                        e.preventDefault();
                        select(TABS[(i + step + TABS.length) % TABS.length].id, true);
                    }
                },
            },
        });
        tabButtons.set(tab.id, button);
        tabList.append(button);
        panels[tab.id].id = `panel-${tab.id}`;
        panels[tab.id].setAttribute('role', 'tabpanel');
        panels[tab.id].setAttribute('aria-labelledby', `tab-${tab.id}`);
    });
    const rightRail = h('aside', { class: 'rail rail-right', aria: { label: 'Inspector, probes and live data' } }, tabList, inspector.element, probes.element, live.element, insight.element);
    select('inspector');
    app.events.on('focus-tab', (id) => panels[id] && select(id));

    // Collapsible rails.
    const shell = h('div', { class: 'shell' }, topbar.element, layers.element, stage, rightRail, status.element);
    const toggleRail = (side) => {
        const cls = side === 'left' ? 'hide-left' : 'hide-right';
        const hidden = shell.classList.toggle(cls);
        (side === 'left' ? leftToggle : rightToggle).setAttribute('aria-pressed', hidden ? 'false' : 'true');
        viewport.requestRender();
    };
    const leftToggle = railToggle('left', () => toggleRail('left'));
    const rightToggle = railToggle('right', () => toggleRail('right'));
    stage.append(leftToggle, rightToggle, hud.element);

    fill(root, shell);
    viewport.init();
    const removeHotkeys = installHotkeys({ app, viewport, topbar, toggleRail, hud });
    app.captureHandler = (message) => viewport.handleCaptureRequest(message);
    installDevUi({ app, viewport });

    const applyTheme = () => {
        document.documentElement.dataset.theme = app.view.theme === 'vellum' ? 'vellum' : 'graphite';
        const tile = app.focusTile();
        document.title = `Atlas · map ${app.view.map}${tile ? ` · tile ${tile.x}, ${tile.y}` : ''}`;
    };
    app.events.on('view', applyTheme);
    applyTheme();

    app.events.on('connection', (c) => {
        if (c.state === 'unauthorized' && !app.stopped) {
            // Once only: the socket close and every refused request all report it, and a late one must not replace
            // the app a fresh token has started since.
            app.stop();
            removeHotkeys();
            forgetToken(window.sessionStorage);
            viewport.dispose();
            showGate('The Atlas host refused this token. Open the link from "arcane-atlas serve" again, or paste a fresh token.');
        }
    });

    if (new URLSearchParams(window.location.search).has('debug')) {
        window.__atlas = { app, viewport, hud };
    }
    app.start();
}

try {
    const token = adoptToken({
        hash: window.location.hash,
        storage: window.sessionStorage,
        clearFragment: () => window.history.replaceState(null, '', `${window.location.pathname}${window.location.search}`),
    });
    if (token) {
        start(token);
    } else {
        showGate('');
    }
} catch (error) {
    showFailure('Atlas could not start', error instanceof Error ? error.message : String(error));
}
