// Materials of ATG1 version 2 chunks (design section 2.3) as the browser draws them.
//
// A chunk's JSON carries a material table; each batch of triangles names one entry. The registry maps a material's `kind` to the
// object that knows how to draw it. Only `generic` is real in lane A1: one texture at slot 0 sampled at uv0, lit or unlit, with the
// blend mode and flags of the material. The kinds of the later lanes (terrain, wmo, m2, skinned, character) are registered as stubs
// so a chunk that names them still draws, as generic, until their lane replaces the stub with `replace`.

/** Blend modes, the wire values (M2 and WMO share them). */
export const BLEND = Object.freeze({
    OPAQUE: 0,
    ALPHA_KEY: 1,
    ALPHA: 2,
    ADDITIVE: 3,
    ADDITIVE_ALPHA: 4,
    MODULATE: 5,
    MODULATE_2X: 6,
});

/** Material flag bits, the wire values. */
export const FLAGS = Object.freeze({
    UNLIT: 0x1,
    UNFOGGED: 0x2,
    TWO_SIDED: 0x4,
    NO_DEPTH_WRITE: 0x8,
    NO_DEPTH_TEST: 0x10,
    CLAMP_S: 0x20,
    CLAMP_T: 0x40,
    USE_COLOR0: 0x80,
    BILLBOARD: 0x100,
    ENV_MAP: 0x200,
});

export class MaterialRegistry {
    #materials = new Map();

    /** Adds the implementation of a kind. Throws when the kind is not a non-empty string or is already registered. */
    register(material) {
        const kind = kindOf(material);
        if (this.#materials.has(kind)) {
            throw new Error(`Material kind '${kind}' is already registered; use replace to override a stub.`);
        }
        this.#materials.set(kind, material);
    }

    /** Replaces the implementation of an already registered kind (a lane taking over its stub). */
    replace(material) {
        const kind = kindOf(material);
        if (!this.#materials.has(kind)) {
            throw new Error(`Material kind '${kind}' is not registered; register it first.`);
        }
        this.#materials.set(kind, material);
    }

    get(kind) {
        return this.#materials.get(kind);
    }

    /** The implementation to draw `kind` with: its own when it is real, `generic` for a stub or an unknown kind. */
    resolve(kind) {
        const material = this.#materials.get(kind);
        return material && !material.stub ? material : this.#materials.get('generic');
    }

    kinds() {
        return this.#materials.keys();
    }
}

function kindOf(material) {
    if (material === null || typeof material !== 'object' || typeof material.kind !== 'string' || material.kind === '') {
        throw new TypeError('A material needs a non-empty string kind.');
    }
    return material.kind;
}

/** The first texture reference of a material at sampler slot 0, or null. */
export function primaryTexture(material) {
    const textures = Array.isArray(material?.textures) ? material.textures : [];
    return textures.find((t) => (t.slot ?? 0) === 0) ?? null;
}

/** True for blend modes that composite over what is drawn (everything but opaque and alpha key). */
export function isBlended(material) {
    return (material?.blend ?? BLEND.OPAQUE) >= BLEND.ALPHA;
}

/**
 * Sets the fixed-function state for a material's blend mode and depth flags. Returns nothing; the caller restores nothing (the next
 * material sets all of it again).
 */
export function applyState(gl, material) {
    const blend = material.blend ?? BLEND.OPAQUE;
    const flags = material.flags ?? 0;
    switch (blend) {
        case BLEND.ALPHA:
            gl.enable(gl.BLEND);
            gl.blendFuncSeparate(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA, gl.ONE, gl.ONE_MINUS_SRC_ALPHA);
            break;
        case BLEND.ADDITIVE:
            gl.enable(gl.BLEND);
            gl.blendFunc(gl.ONE, gl.ONE);
            break;
        case BLEND.ADDITIVE_ALPHA:
            gl.enable(gl.BLEND);
            gl.blendFunc(gl.SRC_ALPHA, gl.ONE);
            break;
        case BLEND.MODULATE:
            gl.enable(gl.BLEND);
            gl.blendFunc(gl.DST_COLOR, gl.ZERO);
            break;
        case BLEND.MODULATE_2X:
            gl.enable(gl.BLEND);
            gl.blendFunc(gl.DST_COLOR, gl.SRC_COLOR);
            break;
        default:
            gl.disable(gl.BLEND);
            break;
    }
    if ((flags & FLAGS.NO_DEPTH_TEST) !== 0) {
        gl.disable(gl.DEPTH_TEST);
    } else {
        gl.enable(gl.DEPTH_TEST);
    }
    gl.depthMask(blend < BLEND.ALPHA && (flags & FLAGS.NO_DEPTH_WRITE) === 0);
}

/**
 * The shader inputs of a `generic` material. `entry` is the TextureStore entry of its primary texture (or null for no texture).
 * ready: sample the texture; missing: sample the checker placeholder; anything else (still loading, starved, no texture): the flat
 * layer colour, so a loading model is neutral grey instead of flashing magenta.
 */
export function genericUniforms(material, entry) {
    const flags = material.flags ?? 0;
    const texture = primaryTexture(material);
    const wrap = Array.isArray(texture?.wrap) ? texture.wrap : [0, 0];
    return {
        textured: entry !== null && (entry.ready || entry.missing),
        checker: entry !== null && entry.missing && !entry.ready,
        unlit: (flags & FLAGS.UNLIT) !== 0,
        unfogged: (flags & FLAGS.UNFOGGED) !== 0,
        alphaKey: material.blend === BLEND.ALPHA_KEY,
        alphaRef: Number.isFinite(material.alphaRef) ? material.alphaRef : 0,
        clampS: wrap[0] === 1 || (flags & FLAGS.CLAMP_S) !== 0,
        clampT: wrap[1] === 1 || (flags & FLAGS.CLAMP_T) !== 0,
        ignoreAlpha: material.blend === BLEND.OPAQUE || material.blend === BLEND.ADDITIVE || material.blend === BLEND.MODULATE || material.blend === BLEND.MODULATE_2X,
    };
}

const GENERIC = Object.freeze({ kind: 'generic', stub: false, uniforms: genericUniforms });
const STUB_KINDS = ['terrain', 'wmo', 'm2', 'skinned', 'character'];

/** A registry with the real `generic` material and a stub for every kind of the later lanes. */
export function createMaterialRegistry() {
    const registry = new MaterialRegistry();
    registry.register(GENERIC);
    for (const kind of STUB_KINDS) {
        registry.register({ kind, stub: true });
    }
    return registry;
}
