# Area: Atlas textured terrain (lane A2)

Status: pending lane A2. Part of [atlas-models](atlas-models.md).

Scope: parse `MCLY`, `MCAL` and `MCSH` from the client ADTs, generate per-tile texture arrays and alpha atlases through the A1 generated
texture registry (`gen:terrain-array/...`, `gen:terrain-alpha/...`), and fill the reserved layer `ref.terrain.tex` with a `terrain`
material. The foundation it builds on (texture store and budget, ATG1 v2, material registry, CPU material registry) is documented in
[atlas-models](atlas-models.md).
