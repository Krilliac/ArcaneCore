// Entity inspector rows: turns a `live.inspect` result (the world daemon's answer, with aura names added by the host) into labelled
// sections for the Insight tab. Pure: the DOM version is `renderInspect`.

import { formatNumber, formatPoint, h, isFiniteNumber } from '../util.js';

const text = (v) => (typeof v === 'string' ? v : '');
const num = (v) => (isFiniteNumber(v) ? v : null);

/** "3.2 s" for a remaining aura time; permanent and unknown are words. */
export function remainingText(ms) {
    if (!isFiniteNumber(ms)) {
        return 'unknown';
    }
    if (ms < 0) {
        return 'permanent';
    }
    return ms >= 60_000 ? `${formatNumber(ms / 60_000, 1)} min` : `${formatNumber(ms / 1000, 1)} s`;
}

/** The unit flags as hex: the bit names belong to the server's UnitFlags enum, which this page does not copy, so none is guessed here. */
export function flagText(flags) {
    return isFiniteNumber(flags) && flags >= 0 ? (flags === 0 ? 'none' : `0x${(flags >>> 0).toString(16).toUpperCase()}`) : 'unknown';
}

/**
 * Sections of [label, text] pairs for a result. A section exists only when the result has something for it, so a non-unit (a game object)
 * shows identity and position only, and a server without auras or a bot seam shows no empty headings.
 */
export function inspectSections(result) {
    if (result === null || typeof result !== 'object') {
        return [];
    }
    const sections = [];
    const identity = [
        ['Name', text(result.name) || '(no name)'],
        ['Kind', text(result.entity) || 'unknown'],
        ['Guid', text(result.guid)],
    ];
    if (num(result.entry) !== null) {
        identity.push(['Entry', String(result.entry)]);
    }
    if (Array.isArray(result.p) && result.p.length === 3 && result.p.every(isFiniteNumber)) {
        identity.push(['Position', formatPoint(result.p, 1)]);
    }
    sections.push({ title: 'Identity', rows: identity });
    if (result.unit !== true) {
        return sections;
    }

    const state = [];
    if (num(result.level) !== null) {
        state.push(['Level', String(result.level)]);
    }
    if (num(result.hp) !== null && num(result.maxHp) !== null) {
        state.push(['Health', `${result.hp} / ${result.maxHp}`]);
    }
    if (result.power && typeof result.power === 'object' && num(result.power.value) !== null) {
        state.push([text(result.power.type) || 'Power', `${result.power.value} / ${num(result.power.max) ?? '?'}`]);
    }
    if (num(result.faction) !== null) {
        state.push(['Faction', String(result.faction)]);
    }
    state.push(['Unit flags', flagText(result.flags)]);
    if (text(result.stand)) {
        state.push(['Stand state', result.stand]);
    }
    state.push(['In combat', result.combat === true ? 'yes' : 'no']);
    if (text(result.target)) {
        state.push(['Target', result.target]);
    }
    if (text(result.victim)) {
        state.push(['Victim', result.victim]);
    }
    sections.push({ title: 'State', rows: state });

    if (Array.isArray(result.threat) && result.threat.length > 0) {
        sections.push({
            title: 'Threat',
            rows: result.threat.slice(0, 10).map((t) => [text(t?.name) || text(t?.g), formatNumber(num(t?.t) ?? 0, 1)]),
        });
    }

    if (result.aurasAvailable === false) {
        sections.push({ title: 'Auras', rows: [['', 'the server has no aura service on this map']] });
    } else if (Array.isArray(result.auras) && result.auras.length > 0) {
        sections.push({
            title: 'Auras',
            rows: result.auras.map((a) => [
                text(a?.name) || `spell ${a?.spell}`,
                `#${a?.spell}${num(a?.stacks) > 1 ? ` x${a.stacks}` : ''} · ${remainingText(a?.remainingMs)}${text(a?.caster) ? ` · by ${a.caster}` : ''}`,
            ]),
        });
    }

    const ai = [];
    if (text(result.ai)) {
        ai.push(['AI', text(result.aiName) ? `${result.ai} (${result.aiName})` : result.ai]);
    }
    if (result.motion && typeof result.motion === 'object') {
        ai.push(['Motion', text(result.motion.current) || 'none']);
        if (Array.isArray(result.motion.stack) && result.motion.stack.length > 0) {
            ai.push(['Motion stack', result.motion.stack.join(' > ')]);
        }
    }
    if (result.bot && typeof result.bot === 'object') {
        ai.push(['Bot route', `${num(result.bot.routePoints) ?? 0} points, next ${num(result.bot.next) ?? 0}`]);
        if (Array.isArray(result.bot.flags) && result.bot.flags.length > 0) {
            ai.push(['Route flags', result.bot.flags.join(', ')]);
        }
        if (Array.isArray(result.bot.goal)) {
            ai.push(['Goal', formatPoint(result.bot.goal, 1)]);
        }
    }
    if (ai.length > 0) {
        sections.push({ title: 'Behaviour', rows: ai });
    }
    return sections;
}

/** The sections as DOM: one heading and a key/value list each. */
export function renderInspect(result) {
    const root = h('div', { class: 'inspect-result' });
    for (const section of inspectSections(result)) {
        const list = h('dl', { class: 'kv' });
        for (const [key, value] of section.rows) {
            list.append(h('dt', { text: key }), h('dd', { text: value }));
        }
        root.append(h('h3', { class: 'section-title', text: section.title }), list);
    }
    return root;
}
