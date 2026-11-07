// The `skinned` material kind (lane A4 stub, filled by lane Y39): animated creature and game-object models. The renderer's vertex layout
// carries position, uv0 and color0 only, so a skinned chunk is drawn in its bind pose, with the same rules as `model` (alpha-key
// reference 0.5). Bone-driven animation needs the boneIndices/boneWeights streams in the renderer's interleaved buffer and a playback
// step; no lane owns those yet (docs/areas/atlas-client-files.md, Deferred deliverables). The animation tables themselves are decoded by
// clientfs/m2.js parseM2Animation. The kind name is real so these chunks stop falling back to an unregistered kind.

import { derivedKind } from './derived.js';
import { registerMaterialKind } from './index.js';
import { withAlphaKeyReference } from './model.js';

// The kind object is built when register() asks for it, not at module evaluation: index.js and this file import each other, and whichever is
// loaded first, register() runs before this module's own constants exist.
/** A new implementation of the `skinned` kind. */
export function skinnedKind() {
    return derivedKind('skinned', { adjust: withAlphaKeyReference });
}

export function register() {
    registerMaterialKind(skinnedKind());
}
