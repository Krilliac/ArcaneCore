// Helper of the kinds that draw through the `generic` program with their own material rules (terrain.js, model.js, skinned.js,
// character.js). The renderer owns the one material program and the vertex layout (position, uv0, color0), so a kind that needs no other
// stream is its rules for the material plus the generic bind: `adjust(material)` returns the material the generic bind should see.
// The adjusted copy is made once per material object (a WeakMap), not once per batch per frame.

import { GENERIC } from './generic.js';

/**
 * @param {string} kind the `material.kind` this implementation answers
 * @param {{adjust: (material: object) => object, pass?: (material: object) => 'opaque'|'alphakey'|'blend'}} rules
 *   `adjust` must return a new object (it is cached per input material); `pass` defaults to the generic ordering of the adjusted material.
 */
export function derivedKind(kind, { adjust, pass = null }) {
    const adjusted = new WeakMap();
    const seen = (material) => {
        let copy = adjusted.get(material);
        if (copy === undefined) {
            copy = adjust(material);
            adjusted.set(material, copy);
        }
        return copy;
    };
    return Object.freeze({
        kind,
        stub: false,
        uniforms: (material, entry) => GENERIC.uniforms(seen(material), entry),
        programs: (gl, shaderLib) => GENERIC.programs(gl, shaderLib),
        layout: (streams) => GENERIC.layout(streams),
        bind: (gl, program, material, ctx, textures) => GENERIC.bind(gl, program, seen(material), ctx, textures),
        pass: pass ?? ((material) => GENERIC.pass(seen(material))),
    });
}
