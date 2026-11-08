// Brings the map feed back after the daemon closed it on its own (an idle timeout, a protocol error, a daemon restart). api.js reconnects the
// WebSocket to the Atlas host, but the host's pipe to the world daemon is a separate connection that nothing re-opened. A close the operator
// asked for (live.disconnect) and the swap of one connection for another ("reconnecting") are not reconnected. The retry delays grow and
// the loop gives up after the last one (with a toast), so a daemon that is gone does not get a connect attempt every second forever.
// The state it works from is read from the host (live.status) when it starts and on every WebSocket hello, because a `live.status` event
// is only pushed on a change: a page opened, reloaded or reconnected while the feed is up has never seen one. It reconnects to the process
// it lost (live.connect with that pid), never to whichever other daemon is left, so a restarted daemon (a new pid) is not followed.

/** The waits between connect attempts, in ms. */
export const RETRY_DELAYS_MS = [1000, 2000, 4000, 8000, 15000, 30000];

/** The status details that mean the feed was ended on purpose or replaced, never lost. */
const INTENTIONAL = new Set(['disconnected', 'reconnecting']);

/** True when a `live.status` that is not connected should be answered with a reconnect. Pure. */
export function shouldReconnect({ wasConnected, status }) {
    return wasConnected && status.connected !== true && !INTENTIONAL.has(status.detail);
}

export class FeedReconnect {
    #off = null;
    #timer = null;
    #attempt = 0;
    #wasConnected = false;
    #running = false;
    #lastPid = null;
    #offHello = null;
    #statusEvents = 0;

    /**
     * @param {object} o
     * @param {import('../app.js').App} o.app
     * @param {(fn: () => void, ms: number) => any} [o.setTimer]
     * @param {(handle: any) => void} [o.clearTimer]
     */
    constructor({ app, setTimer = (fn, ms) => setTimeout(fn, ms), clearTimer = (t) => clearTimeout(t) }) {
        this.app = app;
        this.setTimer = setTimer;
        this.clearTimer = clearTimer;
    }

    /** Number of connect attempts made since the feed was lost. */
    get attempts() {
        return this.#attempt;
    }

    start() {
        this.#off ??= this.app.live.on('status', (status) => this.#onStatus(status));
        this.#offHello ??= this.app.events?.on('connection', (c) => {
            if (c?.state === 'open') {
                this.seed().catch(() => {});
            }
        }) ?? null;
        this.#wasConnected = this.app.live.status.connected === true;
        if (this.#wasConnected) {
            this.#lastPid = this.app.live.status.pid ?? null;
        }
        if (this.app.hasCommand('live.status')) {
            this.seed().catch(() => {});
        }
    }

    /**
     * Reads the feed state from the host and applies it, so the page knows the feed is up (or was lost) without having seen the
     * `live.status` event for it. A status event that lands while the read is in flight is newer and wins.
     * @returns {Promise<boolean>} true when a status was applied
     */
    async seed() {
        const seen = this.#statusEvents;
        const result = await this.app.runCommand('live.status', {}, { quiet: true, noRefresh: true });
        if (!result?.ok || result.data === null || typeof result.data !== 'object' || seen !== this.#statusEvents || this.#off === null) {
            return false;
        }
        this.app.live.applyStatus(result.data);
        return true;
    }

    stop() {
        this.#off?.();
        this.#off = null;
        this.#offHello?.();
        this.#offHello = null;
        this.#lastPid = null;
        this.#cancel();
        this.#attempt = 0;
        this.#wasConnected = false;
    }

    #cancel() {
        if (this.#timer !== null) {
            this.clearTimer(this.#timer);
            this.#timer = null;
        }
    }

    #onStatus(status) {
        this.#statusEvents++;
        if (status.connected) {
            this.#cancel();
            this.#attempt = 0;
            this.#wasConnected = true;
            this.#lastPid = Number.isInteger(status.pid) ? status.pid : this.#lastPid;
            return;
        }
        if (INTENTIONAL.has(status.detail)) {
            // The operator's own disconnect ends the loop; a connection swap is followed by the connected status.
            if (status.detail === 'disconnected') {
                this.#cancel();
                this.#attempt = 0;
                this.#wasConnected = false;
            }
            return;
        }
        if (shouldReconnect({ wasConnected: this.#wasConnected, status }) && this.#timer === null && !this.#running) {
            this.#schedule();
        }
    }

    #schedule() {
        if (this.#attempt >= RETRY_DELAYS_MS.length || !this.app.hasCommand('live.connect')) {
            this.#wasConnected = false;
            this.app.toast?.('The live feed was lost and could not be reconnected. Use live.connect to bring it back.', 'warn');
            return;
        }
        this.#timer = this.setTimer(() => {
            this.#timer = null;
            this.#attemptConnect();
        }, RETRY_DELAYS_MS[this.#attempt]);
    }

    async #attemptConnect() {
        this.#running = true;
        this.#attempt++;
        try {
            const result = await this.app.runCommand('live.connect', this.#lastPid === null ? {} : { pid: this.#lastPid }, { quiet: true, noRefresh: true });
            if (result.ok) {
                await this.app.runCommand('live.status', {}, { quiet: true, noRefresh: true });
            }
        } catch {
            // The connect failed (no daemon, host link down); the next delay is scheduled below.
        } finally {
            this.#running = false;
        }
        if (this.#off !== null && this.#timer === null && this.app.live.status.connected !== true && this.#wasConnected) {
            this.#schedule();
        }
    }
}
