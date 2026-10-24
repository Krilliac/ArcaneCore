// The material registry of the browser (design section 4). A kind is an `impl` ({programs, layout, bind, pass}, see generic.js) that
// knows how to draw the batches of one `material.kind`. The `generic` kind is real here; the files terrain.js (lane A2), model.js
// (A3), skinned.js (A4) and character.js (A5) each export `register()`, which this module calls once it is defined, so a lane fills
// its stub by editing only its own file: import { registerMaterialKind } from './index.js' and call it from register().
// A kind with no implementation (or a stub) draws as generic.

import { register as registerCharacter } from './character.js';
import { BLEND, FLAGS, GENERIC, applyState, genericUniforms, isBlended, primaryTexture } from './generic.js';
import { register as registerModel } from './model.js';
import { register as registerSkinned } from './skinned.js';
import { register as registerTerrain } from './terrain.js';

export { BLEND, FLAGS, applyState, genericUniforms, isBlended, primaryTexture };

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

    /** Replaces the implementation of an already registered kind (a lane taking over a stub). */
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

const shared = new MaterialRegistry();

/** Registers the implementation of a kind (its `impl.kind`) in the shared registry. Throws when the kind is already registered. */
export function registerMaterialKind(impl) {
    shared.register(impl);
}

/** The implementation that draws `kind` in the shared registry: its own, or generic. */
export function materialKind(kind) {
    return shared.resolve(kind);
}

/** The registry every renderer draws through; a kind registered later is seen by renderers that already exist. */
export function createMaterialRegistry() {
    return shared;
}

// Last, after everything above exists: the import cycle with the lane files (they import this module) is then harmless.
registerMaterialKind(GENERIC);
registerTerrain();
registerModel();
registerSkinned();
registerCharacter();
