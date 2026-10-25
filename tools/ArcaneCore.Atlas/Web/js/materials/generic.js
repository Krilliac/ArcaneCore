// The `generic` material of ATG1 version 2 chunks (design sections 2.3 and 4), the one real kind of lane A1: one texture at slot 0
// sampled at uv0, lit or unlit, with the blend mode and flags of the material. It also holds the blend/flag tables and the
// fixed-function helpers every kind shares. Kinds of the later lanes live in their own files (terrain.js, model.js, skinned.js,
// character.js) and register through index.js; until they do, their chunks draw as generic.

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

/** Vertex streams of the expanded batch buffer, in attribute order. The renderer owns the interleaving (stride and offsets). */
const GENERIC_LAYOUT = Object.freeze([
    Object.freeze({ location: 0, stream: 'position', size: 3, type: 'FLOAT', normalized: false, divisor: 0 }),
    Object.freeze({ location: 1, stream: 'uv0', size: 2, type: 'FLOAT', normalized: false, divisor: 0 }),
    Object.freeze({ location: 2, stream: 'color0', size: 4, type: 'UNSIGNED_BYTE', normalized: true, divisor: 0 }),
]);

/**
 * The implementation of the `generic` kind (the `impl` interface of design section 4).
 *   programs(gl, shaderLib) -> {main}   compiled through shaderLib.compile(source); the caller caches them per context
 *   layout(streams) -> attribute descriptors of the streams this kind reads
 *   bind(gl, program, material, ctx, textures) -> boolean   sets the state and uniforms of one batch; false skips the batch.
 *     ctx: {flat: [r, g, b] untextured colour, alpha: layer opacity, fog: boolean, faint: boolean}
 *   pass(material) -> 'opaque' | 'alphakey' | 'blend'
 */
export const GENERIC = Object.freeze({
    kind: 'generic',
    stub: false,
    uniforms: genericUniforms,
    programs(gl, shaderLib) {
        return { main: shaderLib.compile(shaderLib.sources.MATERIAL) };
    },
    layout() {
        return GENERIC_LAYOUT;
    },
    bind(gl, program, material, ctx, textures) {
        const reference = primaryTexture(material);
        const entry = reference && textures ? textures.get(reference.key) : null;
        const u = genericUniforms(material, entry);
        applyState(gl, material);
        if (ctx.faint) {
            gl.enable(gl.BLEND);
            gl.blendFuncSeparate(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA, gl.ONE, gl.ONE_MINUS_SRC_ALPHA);
            gl.depthMask(false);
        }
        gl.uniform3f(program.u.uFlat, ctx.flat[0], ctx.flat[1], ctx.flat[2]);
        gl.uniform1f(program.u.uAlpha, ctx.faint ? ctx.alpha : 1);
        gl.uniform1f(program.u.uAlphaRef, u.alphaRef);
        gl.uniform1i(program.u.uTextured, u.textured ? 1 : 0);
        gl.uniform1i(program.u.uUnlit, u.unlit ? 1 : 0);
        gl.uniform1i(program.u.uAlphaKey, u.alphaKey ? 1 : 0);
        gl.uniform1i(program.u.uIgnoreAlpha, u.ignoreAlpha ? 1 : 0);
        gl.uniform1i(program.u.uUseColor0, ((material.flags ?? 0) & FLAGS.USE_COLOR0) !== 0 ? 1 : 0);
        gl.uniform1i(program.u.uFogOn, ctx.fog && !u.unfogged ? 1 : 0);
        gl.bindTexture(gl.TEXTURE_2D, entry ? entry.texture : textures?.placeholder ?? null);
        if (entry?.ready) {
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, u.clampS ? gl.CLAMP_TO_EDGE : gl.REPEAT);
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, u.clampT ? gl.CLAMP_TO_EDGE : gl.REPEAT);
        }
        return true;
    },
    pass(material) {
        const blend = material.blend ?? BLEND.OPAQUE;
        return blend >= BLEND.ALPHA ? 'blend' : blend === BLEND.ALPHA_KEY ? 'alphakey' : 'opaque';
    },
});

