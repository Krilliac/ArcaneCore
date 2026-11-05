// The `model` material kind (lane A3 stub, filled by lane Y39): static M2 doodads and WMO groups. It draws the batches the model package
// emits through the generic program with the retail alpha-key reference: an alpha-key material that carries no `alphaRef` is cut at
// 0.5 (docs/areas/atlas-models.md, "The alpha-key reference is 0.5"), where the generic kind would keep everything (reference 0).

import { BLEND } from './generic.js';
import { derivedKind } from './derived.js';
import { registerMaterialKind } from './index.js';

/** The material with the alpha-key reference filled in (a copy only when it had to change). */
export function withAlphaKeyReference(material) {
    if (material.blend === BLEND.ALPHA_KEY && !Number.isFinite(material.alphaRef)) {
        return { ...material, alphaRef: 0.5 };
    }
    return { ...material };
}

// The kind object is built when register() asks for it, not at module evaluation: index.js and this file import each other, and whichever is
// loaded first, register() runs before this module's own constants exist.
/** A new implementation of the `model` kind. */
export function modelKind() {
    return derivedKind('model', { adjust: withAlphaKeyReference });
}

export function register() {
    registerMaterialKind(modelKind());
}
