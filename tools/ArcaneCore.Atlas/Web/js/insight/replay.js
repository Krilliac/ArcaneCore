// Replay gating (live insight). While a recording is replayed the UI shows the replayed frames, not the live ones, so the two never fight
// over the same live model. The host publishes `insight.replay.frame` (a frame in the live frame shape, plus `replay: {fields, note}`) and
// `insight.replay.state` ({replaying, playing, seq}); `live.replay action:stop` ends it and live frames count again.

export const REPLAY_FRAME_TOPIC = 'insight.replay.frame';
export const REPLAY_STATE_TOPIC = 'insight.replay.state';

/** What the badge says; the host's own note (on every replayed frame) replaces it once one has arrived. */
export const REPLAY_BADGE = 'replay: positions only';

export const createReplayState = () => ({ active: false, playing: false, seq: 0, frames: 0, note: '', fields: [] });

/** The state after an `insight.replay.state` payload; malformed payloads leave it as it was. */
export function reduceReplayState(state, data) {
    if (data === null || typeof data !== 'object' || typeof data.replaying !== 'boolean') {
        return state;
    }
    return {
        ...state,
        active: data.replaying,
        playing: data.replaying && data.playing === true,
        seq: Number.isFinite(data.seq) ? data.seq : state.seq,
    };
}

/** The state after a replayed frame: the sequence and what the host says the replay does not show. */
export function noteReplayFrame(state, frame) {
    if (frame === null || typeof frame !== 'object' || !Array.isArray(frame.entities)) {
        return state;
    }
    const replay = frame.replay && typeof frame.replay === 'object' ? frame.replay : {};
    return {
        ...state,
        active: true,
        seq: Number.isFinite(frame.seq) ? frame.seq : state.seq,
        frames: state.frames + 1,
        note: typeof replay.note === 'string' ? replay.note : state.note,
        fields: Array.isArray(replay.fields) ? replay.fields.filter((f) => typeof f === 'string') : state.fields,
    };
}

/** Whether a live frame may be applied: not while a replay is on. */
export const acceptsLiveFrame = (state) => !state.active;

/** Whether a replayed frame may be applied: only while a replay is on (a late frame after `stop` is dropped). */
export const acceptsReplayFrame = (state) => state.active;
