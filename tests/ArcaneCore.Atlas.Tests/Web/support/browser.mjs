// Drives a headless Chromium (Chrome or Edge) over the DevTools protocol, using only node's built-in WebSocket client.
// The browser is a child process this module starts and always kills (the whole tree on Windows) in close().

import { execFile, spawn } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

/** Longest a single DevTools request may take; a loaded machine is slow, a wedged browser never answers. */
const COMMAND_TIMEOUT_MS = 60_000;

export class Browser {
    static async launch(executable, { width = 1440, height = 900 } = {}) {
        const profile = fs.mkdtempSync(path.join(os.tmpdir(), 'atlas-browser-'));
        const child = spawn(executable, [
            '--headless=new',
            '--remote-debugging-port=0',
            `--user-data-dir=${profile}`,
            '--no-first-run',
            '--no-default-browser-check',
            '--disable-extensions',
            '--disable-background-networking',
            '--use-angle=swiftshader',
            '--enable-unsafe-swiftshader',
            '--ignore-gpu-blocklist',
            '--force-device-scale-factor=1',
            `--window-size=${width},${height}`,
            'about:blank',
        ], { stdio: 'ignore', windowsHide: true });
        const browser = new Browser(child, profile);
        const portFile = path.join(profile, 'DevToolsActivePort');
        const deadline = Date.now() + 90_000; // generous: several builds and test runs share the machine
        while (!fs.existsSync(portFile)) {
            if (Date.now() > deadline || child.exitCode !== null) {
                await browser.close();
                throw new Error('The browser did not start a DevTools endpoint.');
            }
            await sleep(100);
        }
        // Chromium may still hold the file open while it writes it (EBUSY/EPERM on Windows); retry until the port parses.
        for (;;) {
            await sleep(100);
            try {
                const port = Number(fs.readFileSync(portFile, 'utf8').split(/\r?\n/)[0]);
                if (Number.isInteger(port) && port > 0) {
                    browser.port = port;
                    return browser;
                }
            } catch (error) {
                if (error.code !== 'EBUSY' && error.code !== 'EPERM' && error.code !== 'EACCES' && error.code !== 'ENOENT') {
                    await browser.close();
                    throw error;
                }
            }
            if (Date.now() > deadline || child.exitCode !== null) {
                await browser.close();
                throw new Error('The browser did not publish a DevTools port.');
            }
        }
    }

    constructor(child, profile) {
        this.child = child;
        this.profile = profile;
        this.port = 0;
        this.pages = [];
    }

    async #json(route, method = 'GET') {
        const response = await fetch(`${'http'}://127.0.0.1:${this.port}${route}`, { method, signal: AbortSignal.timeout(COMMAND_TIMEOUT_MS) });
        return response.json();
    }

    /** Opens a tab on `url` and returns a Page wired for console, exception and network failure capture. */
    async newPage(url, { width = 1440, height = 900 } = {}) {
        const target = await this.#json(`/json/new?${encodeURIComponent('about:blank')}`, 'PUT');
        const page = new Page(target.webSocketDebuggerUrl);
        await page.connect();
        await page.send('Page.enable');
        await page.send('Runtime.enable');
        await page.send('Log.enable');
        await page.send('Network.enable');
        await page.send('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile: false });
        await page.send('Page.navigate', { url });
        this.pages.push(page);
        return page;
    }

    async close() {
        for (const page of this.pages) {
            page.ws?.close();
        }
        const pid = this.child.pid;
        if (pid && this.child.exitCode === null) {
            if (process.platform === 'win32') {
                await new Promise((resolve) => execFile('taskkill', ['/PID', String(pid), '/T', '/F'], () => resolve()));
            } else {
                this.child.kill('SIGKILL');
            }
        }
        for (let i = 0; i < 20; i++) {
            try {
                fs.rmSync(this.profile, { recursive: true, force: true });
                break;
            } catch {
                await sleep(100);
            }
        }
    }
}

export class Page {
    constructor(url) {
        this.url = url;
        this.ws = null;
        this.nextId = 1;
        this.pending = new Map();
        this.problems = [];
        this.console = [];
    }

    connect() {
        return new Promise((resolve, reject) => {
            const timer = setTimeout(() => reject(new Error('The DevTools socket did not open in time.')), COMMAND_TIMEOUT_MS);
            this.ws = new WebSocket(this.url);
            this.ws.onopen = () => { clearTimeout(timer); resolve(); };
            this.ws.onerror = () => { clearTimeout(timer); reject(new Error('Could not open the DevTools socket.')); };
            this.ws.onmessage = (event) => this.#message(JSON.parse(event.data));
        });
    }

    #message(m) {
        if (m.id !== undefined) {
            const pending = this.pending.get(m.id);
            if (pending) {
                this.pending.delete(m.id);
                if (m.error) {
                    pending.reject(new Error(`${pending.method}: ${m.error.message}`));
                } else {
                    pending.resolve(m.result);
                }
            }
            return;
        }
        switch (m.method) {
            case 'Runtime.exceptionThrown':
                this.problems.push(`exception: ${m.params.exceptionDetails.exception?.description ?? m.params.exceptionDetails.text}`);
                break;
            case 'Runtime.consoleAPICalled': {
                const text = m.params.args.map((a) => a.value ?? a.description ?? '').join(' ');
                this.console.push({ type: m.params.type, text });
                if (m.params.type === 'error') {
                    this.problems.push(`console.error: ${text}`);
                }
                break;
            }
            case 'Log.entryAdded':
                if (m.params.entry.level === 'error') {
                    this.problems.push(`log ${m.params.entry.source}: ${m.params.entry.text} ${m.params.entry.url ?? ''}`);
                }
                break;
            case 'Network.loadingFailed':
                this.problems.push(`request failed: ${m.params.errorText} ${m.params.blockedReason ?? ''}`);
                break;
            default:
                break;
        }
    }

    /** Sends a DevTools command; rejects after `timeoutMs` so a stalled browser fails this test instead of hanging the run. */
    send(method, params = {}, timeoutMs = COMMAND_TIMEOUT_MS) {
        const id = this.nextId++;
        return new Promise((resolve, reject) => {
            const timer = setTimeout(() => {
                this.pending.delete(id);
                reject(new Error(`${method}: no answer from the browser within ${timeoutMs} ms`));
            }, timeoutMs);
            this.pending.set(id, {
                method,
                resolve: (value) => { clearTimeout(timer); resolve(value); },
                reject: (error) => { clearTimeout(timer); reject(error); },
            });
            this.ws.send(JSON.stringify({ id, method, params }));
        });
    }

    /** Evaluates an expression in the page and returns its JSON value; throws when it throws. */
    async evaluate(expression) {
        const result = await this.send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true });
        if (result.exceptionDetails) {
            throw new Error(`evaluate failed: ${result.exceptionDetails.exception?.description ?? result.exceptionDetails.text}`);
        }
        return result.result.value;
    }

    /** Polls `expression` until it is truthy; throws with `what` and the page problems on timeout. */
    async waitFor(expression, what, timeoutMs = 20_000) {
        const deadline = Date.now() + timeoutMs;
        let last;
        while (Date.now() < deadline) {
            try {
                last = await this.evaluate(expression);
                if (last) {
                    return last;
                }
            } catch (error) {
                last = error.message;
            }
            await sleep(100);
        }
        throw new Error(`Timed out waiting for ${what} (last value: ${JSON.stringify(last)}). Page problems: ${this.problems.join(' | ') || 'none'}`);
    }

    async screenshot() {
        const result = await this.send('Page.captureScreenshot', { format: 'png' });
        return Buffer.from(result.data, 'base64');
    }

    async mouse(type, x, y, { button = 'none', buttons = 0, clickCount = 0, modifiers = 0, deltaX = 0, deltaY = 0 } = {}) {
        await this.send('Input.dispatchMouseEvent', { type, x, y, button, buttons, clickCount, modifiers, deltaX, deltaY });
    }

    async key(type, key, { code = key, modifiers = 0, text } = {}) {
        const special = { Enter: 13, Escape: 27, Tab: 9, Backspace: 8, ArrowLeft: 37, ArrowUp: 38, ArrowRight: 39, ArrowDown: 40 };
        const virtualKey = special[key] ?? (key.length === 1 ? key.toUpperCase().charCodeAt(0) : undefined);
        await this.send('Input.dispatchKeyEvent', { type, key, code, modifiers, text: key === 'Enter' && type === 'keyDown' ? '\r' : text, windowsVirtualKeyCode: virtualKey });
    }

    async press(key, options = {}) {
        await this.key('keyDown', key, { ...options, text: options.text ?? (key.length === 1 ? key : undefined) });
        await this.key('keyUp', key, options);
    }
}

/** Where a browser executable is, from ARCANECORE_TEST_BROWSER (a path), else null. */
export function browserFromEnvironment() {
    const value = process.env.ARCANECORE_TEST_BROWSER;
    return value && fs.existsSync(value) ? value : null;
}
