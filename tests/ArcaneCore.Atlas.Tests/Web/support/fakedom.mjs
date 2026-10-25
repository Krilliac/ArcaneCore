// A small DOM stand-in for the node tests of the public page: elements with children, attributes, text, properties and
// event listeners, a recording canvas context and a controllable WebSocket. It implements only what the page's code
// (util.js `h`/`fill`, the login form and the page) calls; a missing member is a failure of the test, not a silent no-op.

export class FakeNode {
    constructor(tag) {
        this.tagName = String(tag).toUpperCase();
        this.children = [];
        this.parentNode = null;
        this.attributes = new Map();
        this.listeners = new Map();
        this.dataset = {};
        this.style = { setProperty() {} };
        this.className = '';
        this.value = '';
        this.checked = false;
        this.disabled = false;
        this.hidden = false;
        this.focused = false;
        this._text = '';
    }

    get textContent() {
        return this._text + this.children.map((c) => c.textContent).join('');
    }

    set textContent(value) {
        this.children = [];
        this._text = String(value);
    }

    get firstChild() {
        return this.children[0] ?? null;
    }

    get parentElement() {
        return this.parentNode;
    }

    appendChild(child) {
        child.parentNode = this;
        this.children.push(child);
        return child;
    }

    removeChild(child) {
        this.children = this.children.filter((c) => c !== child);
        return child;
    }

    replaceChildren(...nodes) {
        this.children = [];
        this._text = '';
        for (const node of nodes) {
            this.appendChild(node);
        }
    }

    setAttribute(name, value) {
        this.attributes.set(name, String(value));
    }

    getAttribute(name) {
        return this.attributes.get(name) ?? null;
    }

    addEventListener(type, fn) {
        if (!this.listeners.has(type)) {
            this.listeners.set(type, []);
        }
        this.listeners.get(type).push(fn);
    }

    dispatch(type, event = {}) {
        const e = { type, target: this, preventDefault() { this.defaultPrevented = true; }, defaultPrevented: false, ...event };
        for (const fn of this.listeners.get(type) ?? []) {
            fn(e);
        }
        return e;
    }

    focus() {
        this.focused = true;
    }

    getBoundingClientRect() {
        return { left: 0, top: 0, width: 800, height: 600 };
    }

    setPointerCapture() {}

    getContext() {
        this.context ??= new RecordingContext();
        return this.context;
    }

    /** Every descendant (depth first) accepted by the predicate. */
    findAll(predicate) {
        const found = [];
        const walk = (node) => {
            for (const child of node.children) {
                if (predicate(child)) {
                    found.push(child);
                }
                walk(child);
            }
        };
        walk(this);
        return found;
    }

    find(predicate) {
        return this.findAll(predicate)[0] ?? null;
    }

    byTag(tag) {
        return this.findAll((n) => n.tagName === tag.toUpperCase());
    }

    byId(id) {
        return this.find((n) => n.id === id);
    }

    byText(text) {
        return this.find((n) => n.children.length === 0 && n._text === text);
    }
}

class FakeText extends FakeNode {
    constructor(text) {
        super('#text');
        this._text = text;
    }
}

/** Records every call and property set on a 2D context, in order. */
export class RecordingContext {
    constructor() {
        this.calls = [];
        return new Proxy(this, {
            get: (target, name) => {
                if (name in target) {
                    return target[name];
                }
                return (...args) => {
                    target.calls.push([String(name), ...args]);
                };
            },
            set: (target, name, value) => {
                target.calls.push(['set', String(name), value]);
                return true;
            },
        });
    }

    named(name) {
        return this.calls.filter((c) => c[0] === name);
    }

    texts() {
        return this.named('fillText').map((c) => c[1]);
    }
}

/** Installs `document` and `Node` globals and returns the document. */
export function installFakeDom() {
    const document = {
        createElement: (tag) => new FakeNode(tag),
        createTextNode: (text) => new FakeText(String(text)),
        getElementById: () => null,
    };
    globalThis.document = document;
    globalThis.Node = FakeNode;
    return document;
}

/** A WebSocket the test drives: it records what is sent and lets the test open, deliver and close. */
export class FakeWebSocket {
    static instances = [];

    constructor(url) {
        this.url = url;
        this.readyState = 0;
        this.sent = [];
        FakeWebSocket.instances.push(this);
    }

    send(text) {
        this.sent.push(JSON.parse(text));
    }

    close() {
        this.readyState = 3;
        this.onclose?.({ code: 1000 });
    }

    open() {
        this.readyState = 1;
        this.onopen?.();
    }

    deliver(message) {
        this.onmessage?.({ data: JSON.stringify(message) });
    }

    serverClose(code = 1008) {
        this.readyState = 3;
        this.onclose?.({ code });
    }
}
