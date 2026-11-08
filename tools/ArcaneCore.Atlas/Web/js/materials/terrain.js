// The `terrain` material kind (lane A2 stub, filled by lane Y39). Textured terrain chunks (layer ref.terrain.tex) carry one batch per
// MCNK whose material names the chunk's baked texture (clientfs/terrain-tex.js blends the MCLY layers with their MCAL alpha maps on the
// CPU), so the kind draws an opaque, lit, texture clamped to the chunk: the baked texture covers exactly its own MCNK, and a repeat
// at the edge would bleed the opposite side in. Anything the chunk says about blending is ignored: terrain never blends.

import { BLEND, FLAGS } from './generic.js';
import { derivedKind } from './derived.js';
import { registerMaterialKind } from './index.js';

// The kind object is built when register() asks for it, not at module evaluation: index.js and this file import each other, and whichever is
// loaded first, register() runs before this module's own constants exist.
/** A new implementation of the `terrain` kind. */
export function terrainKind() {
    return derivedKind('terrain', {
        adjust: (material) => ({
            ...material,
            blend: BLEND.OPAQUE,
            flags: (material.flags ?? 0) | FLAGS.CLAMP_S | FLAGS.CLAMP_T,
        }),
        pass: () => 'opaque',
    });
}

export function register() {
    registerMaterialKind(terrainKind());
}
