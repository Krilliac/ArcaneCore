// Right rail, Insight tab: the entity inspector, anomaly list and heatmap, record and replay, highlight filters with live watches, and the
// perf HUD switch. Everything here is a thin form over a command (live.inspect, live.anomalies, live.heatmap, live.record, live.replay,
// live.highlight, insight.watch); the same commands work from the CLI and MCP.

import { h } from '../util.js';
import { emptyState } from '../ui/widgets.js';
import { renderInspect } from './inspect.js';
import { REPLAY_BADGE } from './replay.js';
import { WatchController } from './watch.js';

const METRICS = ['anomalies', 'deaths', 'density', 'combat'];
const KINDS = ['', 'player', 'bot', 'creature', 'gameobject'];
const SPEEDS = ['0.5', '1', '2', '4', '8'];

const field = (label, control) => h('label', { class: 'field' }, h('span', { class: 'field-name', text: label }), control);
const select = (options, label, value = '') => {
    const el = h('select', { class: 'select', aria: { label } }, options.map((o) => h('option', { value: o, text: o === '' ? 'any' : o })));
    el.value = value;
    return el;
};
const numberInput = (label, placeholder, value = '') => h('input', { class: 'input num', type: 'text', placeholder, value, spellcheck: false, aria: { label }, attrs: { inputmode: 'decimal' } });
const action = (label, onClick, primary = false) => h('button', { class: `button${primary ? ' button-primary' : ''}`, type: 'button', text: label, on: { click: onClick } });

export class InsightPanel {
    /**
     * @param {import('../app.js').App} app
     * @param {{ toggle: () => boolean }} hud the perf HUD
     */
    constructor(app, hud) {
        this.app = app;
        this.hud = hud;
        this.watch = new WatchController(app);
        this.recording = false;

        this.guid = h('input', { class: 'input', type: 'text', placeholder: 'guid (hex)', spellcheck: false, aria: { label: 'Entity guid' } });
        this.inspected = h('div', { class: 'inspect-box' });
        this.anomalies = h('div', { class: 'insight-out muted' });
        this.heatMetric = select(METRICS, 'Heatmap metric', 'anomalies');
        this.cell = numberInput('Heatmap cell size in yards', 'cell yd', '120');
        this.recordButton = action('Record', () => this.toggleRecord());
        this.recordState = h('div', { class: 'muted num', text: 'not recording' });
        this.badge = h('div', { class: 'replay-badge', hidden: true, text: REPLAY_BADGE, role: 'status' });
        this.scrub = h('input', { class: 'scrub', type: 'range', min: 0, max: 60, step: 1, value: 0, aria: { label: 'Seconds before the newest recorded frame' } });
        this.scrubText = h('span', { class: 'muted num', text: 'live' });
        this.speed = select(SPEEDS, 'Replay speed', '1');
        this.kind = select(KINDS, 'Highlight kind');
        this.name = h('input', { class: 'input', type: 'text', placeholder: 'name contains', spellcheck: false, aria: { label: 'Highlight name filter' } });
        this.hp = numberInput('Highlight hp below percent', 'hp below %');
        this.motion = h('input', { class: 'input', type: 'text', placeholder: 'motion (chase...)', spellcheck: false, aria: { label: 'Highlight motion filter' } });
        this.combat = select(['', 'true', 'false'], 'Highlight in combat');
        this.watchBox = h('input', { type: 'checkbox', aria: { label: 'Keep the highlight following the world' }, on: { change: () => this.#toggleWatch() } });
        this.highlightOut = h('div', { class: 'insight-out muted' });
        this.body = h('div', { class: 'panel-body insight', role: 'region', aria: { label: 'Insight' } });

        this.scrub.addEventListener('input', () => {
            this.scrubText.textContent = this.scrub.value === '0' ? 'newest' : `${this.scrub.value} s ago`;
        });
        app.events.on('selection', (s) => {
            if (s?.entityGuid && document.activeElement !== this.guid) {
                this.guid.value = s.entityGuid;
            }
        });
        app.events.on('commands', () => this.render());
        app.events.on('replay', (state) => this.showReplay(state));
        this.render();
    }

    get element() {
        return this.body;
    }

    render() {
        const { app } = this;
        if (!app.hasCommand('live.inspect')) {
            this.body.replaceChildren(emptyState('Live insight is not available', 'The host has no live module, or the world server it is connected to predates live insight.'));
            return;
        }
        this.body.replaceChildren(
            this.#section('Inspect an entity', [
                h('div', { class: 'row' }, this.guid, action('Inspect', () => this.inspect(), true)),
                this.inspected,
            ]),
            this.#section('Anomalies', [
                h('div', { class: 'row' }, action('List and mark', () => this.listAnomalies()), action('Clear', () => this.clearOverlays())),
                h('div', { class: 'row' }, field('Heatmap of', this.heatMetric), field('Cell', this.cell), action('Draw', () => this.heatmap())),
                this.anomalies,
            ]),
            this.#section('Record and replay', [
                h('div', { class: 'row' }, this.recordButton, this.recordState),
                this.badge,
                h('div', { class: 'row' }, this.scrub, this.scrubText),
                h('div', { class: 'row' },
                    action('Show', () => this.replayAt()),
                    action('Play', () => this.replay('play')),
                    action('Pause', () => this.replay('pause')),
                    action('Live', () => this.replay('stop')),
                    field('Speed', this.speed)),
            ]),
            this.#section('Highlight', [
                h('div', { class: 'row' }, field('Kind', this.kind), field('In combat', this.combat)),
                h('div', { class: 'row' }, this.name, this.hp, this.motion),
                h('div', { class: 'row' }, action('Highlight', () => this.highlight(), true), h('label', { class: 'check' }, this.watchBox, h('span', { text: 'follow live' }))),
                this.highlightOut,
            ]),
            this.#section('Performance', [h('div', { class: 'row' }, action('Show or hide the HUD (P)', () => this.hud.toggle()))]),
        );
    }

    clearOverlays() {
        for (const name of ['live.anomalies', 'live.heatmap', 'live.highlight', 'live.inspect']) {
            this.app.overlays.remove(name);
        }
        this.app.events.emit('overlays');
    }

    #section(title, children) {
        return h('section', { class: 'panel-section' }, h('h3', { class: 'section-title', text: title }), ...children);
    }

    async inspect() {
        const guid = this.guid.value.trim();
        if (!guid) {
            this.inspected.replaceChildren(emptyState('Enter a guid', 'Select an entity in the Live tab, or paste a guid.'));
            return;
        }
        const result = await this.app.runCommand('live.inspect', { guid }, { noRefresh: true });
        this.inspected.replaceChildren(result.ok ? renderInspect(result.data) : emptyState('Could not inspect', result.error?.message ?? 'the command failed'));
    }

    async listAnomalies() {
        const result = await this.app.runCommand('live.anomalies', { limit: 200 }, { noRefresh: true });
        if (!result.ok) {
            this.anomalies.textContent = result.error?.message ?? 'failed';
            return;
        }
        const d = result.data;
        this.anomalies.textContent = `${d.returned} of ${d.total} anomalies${d.truncated ? ' (more than shown)' : ''}; ${d.overlay?.points?.length ?? 0} marked on the map.`;
    }

    async heatmap() {
        const cellSize = Number(this.cell.value);
        const args = { metric: this.heatMetric.value };
        if (Number.isFinite(cellSize) && cellSize > 0) {
            args.cellSize = cellSize;
        }
        const result = await this.app.runCommand('live.heatmap', args, { noRefresh: true });
        this.anomalies.textContent = result.ok
            ? `${result.data.total} counted in ${result.data.cells} cells${result.data.truncated ? `; ${result.data.other.cells} quiet cells merged` : ''}.`
            : (result.error?.message ?? 'failed');
    }

    async toggleRecord() {
        const result = await this.app.runCommand('live.record', { on: !this.recording }, { noRefresh: true });
        if (result.ok) {
            this.recording = result.data.recording === true;
            this.recordButton.textContent = this.recording ? 'Stop recording' : 'Record';
            const frames = result.data.frames ?? 0;
            this.recordState.textContent = this.recording ? `recording, ${frames} frames` : `stopped, ${frames} frames kept`;
        }
    }

    async replayAt() {
        const at = Number(this.scrub.value);
        await this.app.runCommand('live.replay', { at: Number.isFinite(at) ? at : 0 }, { noRefresh: true });
    }

    async replay(actionName) {
        const args = { action: actionName };
        if (actionName === 'play') {
            args.speed = Number(this.speed.value);
            args.at = Number(this.scrub.value) || 0;
        }
        await this.app.runCommand('live.replay', args, { noRefresh: true });
    }

    showReplay(state) {
        this.badge.hidden = !state.active;
        this.badge.textContent = state.note || REPLAY_BADGE;
        if (state.active) {
            this.recordState.textContent = `replaying frame ${state.seq}${state.playing ? '' : ' (paused)'}`;
        }
    }

    #highlightArgs() {
        const args = {};
        if (this.kind.value) args.kind = this.kind.value;
        if (this.name.value.trim()) args.name = this.name.value.trim();
        const hp = Number(this.hp.value);
        if (this.hp.value.trim() && Number.isFinite(hp)) args.hpBelow = hp;
        if (this.motion.value.trim()) args.motion = this.motion.value.trim();
        if (this.combat.value) args.inCombat = this.combat.value === 'true';
        return args;
    }

    async highlight() {
        const args = this.#highlightArgs();
        if (Object.keys(args).length === 0) {
            this.highlightOut.textContent = 'Give at least one filter.';
            return;
        }
        const result = await this.app.runCommand('live.highlight', args, { noRefresh: true });
        this.highlightOut.textContent = result.ok
            ? `${result.data.returned} of ${result.data.total} entities ringed${result.data.truncated ? ' (capped)' : ''}.`
            : (result.error?.message ?? 'failed');
        if (this.watchBox.checked) {
            await this.#toggleWatch();
        }
    }

    async #toggleWatch() {
        if (!this.watchBox.checked) {
            await this.watch.stop();
            return;
        }
        const args = this.#highlightArgs();
        if (Object.keys(args).length === 0 || !(await this.watch.start('live.highlight', args, 1))) {
            this.watchBox.checked = false;
        }
    }

    setActive() {}
}
