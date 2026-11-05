// Brings the map feed back after the daemon closed it on its own (an idle timeout, a protocol error, a daemon restart). api.js reconnects the
// WebSocket to the Atlas host, but the host's pipe to the world daemon is a separate connection that nothing re-opened. A close the operator
// asked for (live.disconnect) and the swap of one connection for another ("reconnecting") are not reconnected. The retry delays grow and
// the loop gives up after the last one, so a daemon that is gone does not get a connect attempt every second forever.

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
        this.#wasConnected = this.app.live.status.connected === true;
    }

    stop() {
        this.#off?.();
        this.#off = null;
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
        if (status.connected) {
            this.#cancel();
            this.#attempt = 0;
            this.#wasConnected = true;
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
            const result = await this.app.runCommand('live.connect', {}, { quiet: true, noRefresh: true });
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
