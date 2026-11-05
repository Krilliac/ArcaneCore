// The `character` material kind (lane A5 stub, filled by lane Y39): player and bot character models. Like `skinned` it draws the bind
// pose through the generic program with the alpha-key reference of `model` (the renderer's layout has no bone streams yet); the baked
// body, face and hair textures arrive as ordinary texture keys of the material.

import { derivedKind } from './derived.js';
import { registerMaterialKind } from './index.js';
import { withAlphaKeyReference } from './model.js';

// The kind object is built when register() asks for it, not at module evaluation: index.js and this file import each other, and whichever is
// loaded first, register() runs before this module's own constants exist.
/** A new implementation of the `character` kind. */
export function characterKind() {
    return derivedKind('character', { adjust: withAlphaKeyReference });
}

export function register() {
    registerMaterialKind(characterKind());
}
