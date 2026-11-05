// The `wmo` and `m2` material kinds (wire names of Core/Materials/Material.cs Kinds; lane A3 stubs, filled by lane Y39): world-model
// groups and static M2 doodads. Both draw the batches the model package emits through the generic program with the retail alpha-key
// reference: an alpha-key material that carries no `alphaRef` is cut at 0.5 (docs/areas/atlas-models.md, "The alpha-key reference is
// 0.5"), where the generic kind would keep everything (reference 0). The same rule is shared by `skinned` and `character`.

import { BLEND } from './generic.js';
import { derivedKind } from './derived.js';
import { registerMaterialKind } from './index.js';

/** The material with the alpha-key reference filled in (always a copy: derivedKind caches the copy per material object). */
export function withAlphaKeyReference(material) {
    if (material.blend === BLEND.ALPHA_KEY && !Number.isFinite(material.alphaRef)) {
        return { ...material, alphaRef: 0.5 };
    }
    return { ...material };
}

/** The wire kinds this file implements. */
export const MODEL_KINDS = Object.freeze(['wmo', 'm2']);

// The kind objects are built when register() asks for them, not at module evaluation: index.js and this file import each other, and
// whichever is loaded first, register() runs before this module's own constants exist.
/** A new implementation of the `wmo` kind. */
export function wmoKind() {
    return derivedKind('wmo', { adjust: withAlphaKeyReference });
}

/** A new implementation of the `m2` kind. */
export function m2Kind() {
    return derivedKind('m2', { adjust: withAlphaKeyReference });
}

export function register() {
    registerMaterialKind(wmoKind());
    registerMaterialKind(m2Kind());
}
