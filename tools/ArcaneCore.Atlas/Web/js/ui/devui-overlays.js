// Debug overlays for one creature (aggro and leash rings, home, spawn, route, threat lines, AI name). The data comes from the
// `action.on template=debug` command, whose result carries an `overlay` that the existing overlay path draws (app.runCommand hands every
// result to the overlay store). While a creature is watched the command is re-run at no more than 2 Hz. insight.watch only re-runs the
// commands it lists as watchable (live.anomalies, live.heatmap, live.highlight), which does not include action.on, so this module keeps its own timer.

/** The fastest refresh: twice a second. */
export const MIN_INTERVAL_MS = 500;

/** The overlay store key the debug result is drawn under (the command name). */
export const OVERLAY_KEY = 'action.on';

export class DebugOverlays {
    #guid = null;
    #timer = null;
    #busy = false;

    /**
     * @param {object} o
     * @param {import('../app.js').App} o.app
     * @param {number} [o.intervalMs]  clamped to at least 500
     * @param {(fn: () => void, ms: number) => any} [o.setTimer]
     * @param {(handle: any) => void} [o.clearTimer]
     */
    constructor({ app, intervalMs = MIN_INTERVAL_MS, setTimer = (fn, ms) => setInterval(fn, ms), clearTimer = (t) => clearInterval(t) }) {
        this.app = app;
        this.intervalMs = Math.max(MIN_INTERVAL_MS, intervalMs);
        this.setTimer = setTimer;
        this.clearTimer = clearTimer;
    }

    /** The guid being watched, or null. */
    get watched() {
        return this.#guid;
    }

    /** Starts (or restarts on another creature) the overlay and shows it at once. */
    async watch(guid) {
        this.stop();
        this.#guid = guid;
        this.#timer = this.setTimer(() => this.refresh(), this.intervalMs);
        return this.refresh();
    }

    /** Runs the command once; stops the watch and says why when the daemon refuses. */
    async refresh() {
        const guid = this.#guid;
        if (guid === null || this.#busy) {
            return false;
        }
        this.#busy = true;
        try {
            const result = await this.app.runCommand('action.on', { template: 'debug', guid }, { quiet: true, noRefresh: true });
            if (this.#guid !== guid) {
                return false;
            }
            const data = result.ok ? result.data : null;
            if (!data || data.ok !== true) {
                const why = data?.message ?? result.error?.message ?? 'the daemon refused';
                this.stop();
                this.app.toast(`The AI overlay stopped: ${why}`, 'warn');
                return false;
            }
            return true;
        } finally {
            this.#busy = false;
        }
    }

    /** Stops refreshing and removes the drawn overlay. */
    stop() {
        if (this.#timer !== null) {
            this.clearTimer(this.#timer);
            this.#timer = null;
        }
        if (this.#guid !== null) {
            this.#guid = null;
            this.app.overlays.remove(OVERLAY_KEY);
            this.app.events.emit('overlays');
        }
    }

    /** Watches `guid`, or stops when it is the one already watched. */
    async toggle(guid) {
        if (this.#guid === guid) {
            this.stop();
            return false;
        }
        return this.watch(guid);
    }
}
