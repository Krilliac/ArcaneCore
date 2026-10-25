# Area: Atlas textured WMOs and static M2 doodads (lane A3)

Status: pending lane A3. Part of [atlas-models](atlas-models.md).

Scope: read `MOTX`, `MOMT`, `MOTV`, `MOCV`, `MONR` and `MOBA` from WMOs and the full M2 material tables, fill the reserved layers
`ref.wmo.tex` and `ref.m2.tex`, extend the model package to every M2 view and to WMOs, and add `model.preview` (a CPU render of one
package). The foundation it builds on (texture store, ATG1 v2, the `generic` material, the minimal M2 package) is documented in
[atlas-models](atlas-models.md).
