// Watches: `insight.watch` re-runs a command on every live frame (at most 2 times a second) so its overlay follows the world. A command is
// written 'name' or 'name {json arguments}'. Only the commands that cap their own result can be watched.

export const WATCHABLE = ['live.anomalies', 'live.heatmap', 'live.highlight'];
export const MAX_HZ = 2;

/** The string `insight.watch` takes for a command and its arguments (empty or absent arguments give the bare name). */
export function watchSpec(name, args) {
    if (!WATCHABLE.includes(name)) {
        throw new Error(`${name} cannot be watched; the watchable commands are ${WATCHABLE.join(', ')}.`);
    }
    const keys = args ? Object.keys(args).filter((k) => args[k] !== undefined && args[k] !== null && args[k] !== '') : [];
    if (keys.length === 0) {
        return name;
    }
    const clean = {};
    for (const key of keys) {
        clean[key] = args[key];
    }
    return `${name} ${JSON.stringify(clean)}`;
}

/** Clamps a requested rate into the range the host accepts. */
export const watchRate = (hz) => (Number.isFinite(hz) && hz > 0 ? Math.min(hz, MAX_HZ) : 1);

/** Starts and stops one watch through the app's command channel; remembers the id the host gave it. */
export class WatchController {
    #id = null;

    constructor(app) {
        this.app = app;
    }

    get active() {
        return this.#id !== null;
    }

    /** Starts (replacing any watch of this controller) a watch of one command. Resolves true when the host accepted it. */
    async start(name, args, hz = 1) {
        await this.stop();
        const result = await this.app.runCommand('insight.watch', { action: 'start', commands: [watchSpec(name, args)], hz: watchRate(hz) }, { noRefresh: true });
        this.#id = result.ok && typeof result.data?.id === 'string' ? result.data.id : null;
        return this.#id !== null;
    }

    async stop() {
        if (this.#id === null) {
            return true;
        }
        const id = this.#id;
        this.#id = null;
        const result = await this.app.runCommand('insight.watch', { action: 'stop', id }, { quiet: true, noRefresh: true });
        return result.ok;
    }
}
