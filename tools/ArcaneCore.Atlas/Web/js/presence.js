// Presence of the other UI sessions: where each one is looking, as the host relays it (`ui.presence`, `ui.presence.left` and
// the hello `sessions` list). Pure logic with no DOM access, so node tests import it directly.

import { eye as cameraEye } from './camera.js';
import { Emitter, isFiniteNumber, packRgba, wrapDegrees } from './util.js';

/** Label colour of a peer: amber, which no layer or overlay uses. */
export const PEER_COLOR = packRgba(255, 176, 32);

/** The same amber for 2D canvas drawing. */
export const PEER_CSS_COLOR = '#FFB020';

/** Label priority of a peer; overlays use 4, so a peer wins an overlap. */
export const PEER_PRIORITY = 6;

/** Largest absolute coordinate the host accepts in a presence message. */
export const MAX_COORDINATE = 100000;

/** Longest session name the host accepts. */
export const NAME_MAX = 32;

const MODES = new Set(['orbit', 'fly', 'top']);

/** A display name as the host stores it: no control or format characters (bidi overrides, zero-width marks), trimmed, at most NAME_MAX chars. */
export function cleanName(name) {
    // With the u flag a lone surrogate is a code point of its own (category Cs), which the host cannot carry either.
    let text = String(name ?? '').replace(/[\p{Cc}\p{Cf}\p{Cs}]/gu, '').trim();
    if (text.length > NAME_MAX) {
        text = text.slice(0, NAME_MAX);
        if (/[\uD800-\uDBFF]$/.test(text)) {
            text = text.slice(0, -1);
        }
        text = text.trimEnd();
    }
    return text;
}

const bound = (v) => Math.max(-MAX_COORDINATE, Math.min(MAX_COORDINATE, v));

/** One host presence entry as {id, name, map, at:[x,y,z], yaw, mode, kind}, or null when it is malformed. `kind` is 'agent' for a scripted session and 'ui' otherwise (also for a host that sends none). */
export function normalizePresence(raw) {
    if (!raw || typeof raw !== 'object' || typeof raw.id !== 'string' || raw.id.length === 0) {
        return null;
    }
    if (!Number.isInteger(raw.map) || raw.map < 0 || raw.map > 0xFFFFFFFF) {
        return null;
    }
    if (!Array.isArray(raw.at) || raw.at.length !== 3 || !raw.at.every(isFiniteNumber)) {
        return null;
    }
    if (!isFiniteNumber(raw.yaw) || !MODES.has(raw.mode)) {
        return null;
    }
    const name = typeof raw.name === 'string' && raw.name.trim().length > 0 ? raw.name : `session ${raw.id.slice(0, 4)}`;
    return { id: raw.id, name, map: raw.map, at: [raw.at[0], raw.at[1], raw.at[2]], yaw: wrapDegrees(raw.yaw), mode: raw.mode, kind: raw.kind === 'agent' ? 'agent' : 'ui' };
}

const samePeer = (a, b) => a.name === b.name && a.map === b.map && a.yaw === b.yaw && a.mode === b.mode
    && a.kind === b.kind && a.at[0] === b.at[0] && a.at[1] === b.at[1] && a.at[2] === b.at[2];

/** The other sessions, keyed by connection id. */
export class PresenceStore extends Emitter {
    #selfId = null;
    #peers = new Map();

    /** Replaces everything with the hello list; entries of this connection or malformed ones are skipped. */
    reset(selfId, sessions) {
        this.#selfId = selfId ?? null;
        this.#peers.clear();
        for (const raw of Array.isArray(sessions) ? sessions : []) {
            const peer = normalizePresence(raw);
            if (peer && peer.id !== this.#selfId) {
                this.#peers.set(peer.id, peer);
            }
        }
    }

    /** Adds or updates one peer; true when the store changed (own id and malformed entries are ignored). */
    apply(raw) {
        const peer = normalizePresence(raw);
        if (!peer || peer.id === this.#selfId) {
            return false;
        }
        const old = this.#peers.get(peer.id);
        if (old && samePeer(old, peer)) {
            return false;
        }
        this.#peers.set(peer.id, peer);
        return true;
    }

    /** Drops one peer; true when it was known. */
    remove(id) {
        return this.#peers.delete(id);
    }

    peers() {
        return [...this.#peers.values()];
    }

    /** The peers that are on `map`. */
    onMap(map) {
        return this.peers().filter((p) => p.map === map);
    }
}

/**
 * The outgoing `presence` message for a view: the eye in fly mode, else the camera target, clamped to the range the host
 * accepts. `name` is always sent; '' asks the host for this connection's default name.
 */
export function presenceMessage(view, name) {
    const camera = view.camera;
    const at = camera.mode === 'fly' ? cameraEye(camera) : camera.target;
    return {
        op: 'presence',
        name: typeof name === 'string' ? name : '',
        map: view.map,
        at: [bound(at[0]), bound(at[1]), bound(at[2])],
        yaw: camera.yaw,
        mode: camera.mode,
    };
}

/** Label candidates (for layoutLabels) for the peers on `map`. */
export function peerLabelCandidates(peers, map) {
    return peers
        .filter((p) => p.map === map)
        .map((p) => ({ at: p.at, text: p.name, color: PEER_COLOR, priority: PEER_PRIORITY }));
}
