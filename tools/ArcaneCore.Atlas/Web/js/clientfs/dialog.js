// The first-entry dialog and the "Re-allow access" banner of the client-file reader (design 0.5). Pure DOM: no storage, no
// parsing; index.js wires the buttons. Nodes are built with createElement/textContent (the page's CSP forbids inline script
// and style, and the repo forbids markup strings); the look comes from app.css (.backdrop, .dialog, .notice).

/** Builds an element: h('p', {class: 'x', text: '...', attrs: {...}}, ...children). */
export function h(tag, { class: className = null, text = null, attrs = null } = {}, ...children) {
    const node = document.createElement(tag);
    if (className) {
        node.className = className;
    }
    if (text !== null) {
        node.textContent = text;
    }
    for (const [name, value] of Object.entries(attrs ?? {})) {
        node.setAttribute(name, value);
    }
    for (const child of children) {
        node.append(child);
    }
    return node;
}

const button = (label, onClick, primary = false) => {
    const node = h('button', { class: primary ? 'button button-primary' : 'button', text: label, attrs: { type: 'button' } });
    node.addEventListener('click', onClick);
    return node;
}

/** Shows or clears the error line inside `host` (an element with data-clientfs=error exists only while there is an error). */
export function setErrorLine(host, message) {
    host.querySelector('[data-clientfs="error"]')?.remove();
    if (message) {
        const anchor = host.querySelector('[data-clientfs-actions]');
        anchor.parentNode.insertBefore(h('p', { class: 'notice notice-error', text: message, attrs: { 'data-clientfs': 'error', role: 'alert' } }), anchor);
    }
}

/**
 * Asks the browser for a folder the portable way: an `<input type=file webkitdirectory>` kept in `host`, opened by a click.
 * `onFiles` receives the picked files as an array. The input is reused if it exists.
 */
export function openFolderInput(host, onFiles) {
    let input = host.querySelector('input[type="file"][webkitdirectory]');
    if (input === null) {
        input = h('input', { attrs: { type: 'file', webkitdirectory: '', multiple: '', hidden: '', 'aria-hidden': 'true', tabindex: '-1' } });
        input.addEventListener('change', () => {
            const files = Array.from(input.files ?? []);
            // Cleared after the handler returns so choosing the same folder again fires change; the Files are already copied.
            setTimeout(() => { input.value = ''; }, 0);
            onFiles(files);
        });
        host.append(input);
    }
    input.click();
}

/**
 * @param {object} handlers
 * @param {() => void} handlers.onChoose the Choose folder button (runs inside the click, so the picker has its user gesture)
 * @param {() => void} handlers.onSkip
 * @param {() => void} handlers.onDismiss Escape: close without remembering a choice
 */
export function createFirstEntryDialog({ onChoose, onSkip, onDismiss }) {
    const title = h('h2', { class: 'dialog-title', text: 'Draw the map with your own game files', attrs: { id: 'clientfs-title' } });
    const actions = h('div', { attrs: { 'data-clientfs-actions': '' } });
    const body = h('div', { class: 'dialog-body' },
        h('p', { text: 'Atlas draws the world from your own World of Warcraft 1.12.1 (build 5875) installation.' }),
        h('p', { text: 'Choose your WoW folder (the one that contains WoW.exe and the Data folder).' }),
        h('p', { class: 'muted', text: 'Files are read in this browser only and are never uploaded.' }),
        h('p', { class: 'muted', text: 'You can skip this and see an untextured map.' }),
        actions);
    const element = h('div', { class: 'dialog', attrs: { role: 'dialog', 'aria-modal': 'true', 'aria-labelledby': 'clientfs-title', 'data-clientfs': 'dialog' } },
        h('div', { class: 'dialog-head' }, title), body);
    const backdrop = h('div', { class: 'backdrop' }, element);

    const choose = button('Choose folder', () => onChoose(), true);
    const skip = button('Skip', () => onSkip());
    let why = null;
    const whyButton = button('Why?', () => {
        if (why === null) {
            why = h('p', {
                class: 'muted',
                text: 'Atlas reads only the archives in Data/*.MPQ and the version of WoW.exe. Your WTF, Interface, Logs and Screenshots folders are never opened. '
                    + 'Parsed results are kept in a cache in this browser (private to this site) so the next visit is quick; Forget folder in the settings deletes it.',
            });
            body.insertBefore(why, actions);
        } else {
            why.remove();
            why = null;
        }
    });
    actions.append(choose, skip, whyButton);
    backdrop.addEventListener('keydown', (event) => {
        if (event.key === 'Escape') {
            onDismiss();
        }
    });

    return {
        element,
        backdrop,
        show() {
            document.body.append(backdrop);
            choose.focus();
        },
        close() {
            backdrop.remove();
        },
        setError: (message) => setErrorLine(body, message),
        setBusy(busy) {
            for (const node of [choose, skip]) {
                node.disabled = busy;
            }
        },
    };
}

/** A small modal built from the shared dialog classes: head, body paragraphs, an actions row. */
function createPanel(dataName, titleText, paragraphs, buttons, onEscape) {
    const actions = h('div', { class: 'button-row', attrs: { 'data-clientfs-actions': '' } });
    const body = h('div', { class: 'dialog-body' }, ...paragraphs, actions);
    const element = h('div', { class: 'dialog', attrs: { role: 'dialog', 'aria-modal': 'true', 'aria-label': titleText, 'data-clientfs': dataName } },
        h('div', { class: 'dialog-head' }, h('h2', { class: 'dialog-title', text: titleText })), body);
    const backdrop = h('div', { class: 'backdrop' }, element);
    actions.append(...buttons);
    backdrop.addEventListener('keydown', (event) => {
        if (event.key === 'Escape') {
            onEscape();
        }
    });
    return { element, body, actions, backdrop };
}

/** The prompt shown when the browser needs one click to give the stored folder handle its permission back. */
export function createReallowBanner({ onReallow, onLater }) {
    const reallow = button('Re-allow access', () => onReallow(), true);
    const { element, backdrop } = createPanel('reallow', 'Allow access to your WoW folder',
        [h('p', { text: 'Atlas needs your permission to read your WoW folder again. Your files stay on this computer.' })],
        [reallow, button('Not now', () => onLater())], () => onLater());
    return {
        element,
        show() {
            document.body.append(backdrop);
            reallow.focus();
        },
        close() {
            backdrop.remove();
        },
        setError: (message) => setErrorLine(element, message),
    };
}

/**
 * The settings panel. `values` is {folderName, build, version, cacheText, budget: {min, max, value, step}, budgetText};
 * `handlers` is {onChange(), onForget(), onBudget(bytes), onClose()}.
 */
export function createSettingsPanel(values, { onChange, onForget, onBudget, onClose }) {
    const line = (label, value) => h('p', {}, h('span', { class: 'muted', text: `${label}: ` }), h('span', { text: value }));
    const budgetLabel = h('span', { text: values.budgetText });
    const slider = h('input', { attrs: { type: 'range', 'data-clientfs': 'budget', min: String(values.budget.min), max: String(values.budget.max), step: String(values.budget.step), value: String(values.budget.value), 'aria-label': 'Cache size limit' } });
    slider.addEventListener('input', () => {
        onBudget(Number(slider.value), budgetLabel);
    });
    slider.addEventListener('change', () => {
        onBudget(Number(slider.value), budgetLabel);
    });
    const cacheLine = line('Cache in use', values.cacheText);
    const panel = createPanel('settings', 'Game files',
        [line('Folder', values.folderName), line('Build', `${values.build} (${values.version})`), cacheLine,
            h('p', {}, h('span', { class: 'muted', text: 'Cache limit: ' }), budgetLabel), slider],
        [button('Change folder', () => onChange(), true), button('Forget folder', () => onForget()), button('Close', () => onClose())], () => onClose());
    return {
        element: panel.element,
        show() {
            document.body.append(panel.backdrop);
        },
        close() {
            panel.backdrop.remove();
        },
        setError: (message) => setErrorLine(panel.body, message),
        setBudgetValue(value) {
            slider.value = String(value);
        },
    };
}
