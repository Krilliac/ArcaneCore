# Area: Atlas textured terrain (lane A2)

Status: delivered for the browser by lane Y39 (client-file reader); the host-side generated arrays below are still not built. Part of [atlas-models](atlas-models.md).

Scope: parse `MCLY`, `MCAL` and `MCSH` from the client ADTs, generate per-tile texture arrays and alpha atlases through the A1 generated
texture registry (`gen:terrain-array/...`, `gen:terrain-alpha/...`), and fill the reserved layer `ref.terrain.tex` with a `terrain`
material. The foundation it builds on (texture store and budget, ATG1 v2, material registry, CPU material registry) is documented in
[atlas-models](atlas-models.md).

## What is delivered (browser, lane Y39)

Textured terrain is built in the browser from the visitor's own client files, not through `gen:terrain-array/...` or `gen:terrain-alpha/...`:
`clientfs/terrain-tex.js` reads MTEX, MCLY and MCAL, bakes one 128x128 texture per MCNK (base layer, then each further layer lerped in by its
4-bit alpha map, layers tiled 8 times across the chunk) and serves the `ref.terrain.tex` tile as ATG1 version 2 with one `terrain` material per
MCNK (`materials/terrain.js`: opaque, lit, clamped). Details, limits and the Worker operations are in [atlas-client-files](atlas-client-files.md).

Not delivered: MCSH shadow maps, the host-side texture arrays and alpha atlas (the host has no source for the reserved layer `ref.terrain.tex`, so it answers
NotApplicable or does not serve it, and the Local page falls back to the reader), and a real-client check of the alpha-map orientation.
