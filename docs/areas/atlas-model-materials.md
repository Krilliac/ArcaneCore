# Area: Atlas textured WMOs and static M2 doodads (lane A3)

Status: pending lane A3 for the WMO and M2 readers; the `wmo` and `m2` material kinds themselves are delivered by lane Y39 (below). Part of [atlas-models](atlas-models.md).

Scope: read `MOTX`, `MOMT`, `MOTV`, `MOCV`, `MONR` and `MOBA` from WMOs and the full M2 material tables, fill the reserved layers
`ref.wmo.tex` and `ref.m2.tex`, extend the model package to every M2 view and to WMOs, and add `model.preview` (a CPU render of one
package). The foundation it builds on (texture store, ATG1 v2, the `generic` material, the minimal M2 package) is documented in
[atlas-models](atlas-models.md).

## Material kinds (browser, lane Y39)

`materials/model.js` registers the real kinds `wmo` and `m2` (the wire names of `Core/Materials/Material.cs`; there is no kind called `model`): they draw through the generic program and cut an alpha-key material that carries no `alphaRef` at 0.5 (the generic
kind keeps everything). `materials/skinned.js` and `materials/character.js` are real kinds with the same rules (shared through `withAlphaKeyReference`); the renderer's vertex layout holds
position, uv0 and color0 only, so skinned and character chunks draw in their bind pose until the renderer has bone streams and an animation playback step (no lane owns that yet; the animation tables are decoded by `clientfs/m2.js`, see [atlas-client-files](atlas-client-files.md)).
`materials/terrain.js` is described in [atlas-terrain-textures](atlas-terrain-textures.md). The shared helper is `materials/derived.js`.
