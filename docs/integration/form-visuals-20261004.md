# Catalog-backed form visuals (2026-10-04)

ModShapeshift applies the pinned FormDisplayTable values when the supplied
SpellShapeshiftForm catalog contains the selected druid form. StanceFeature
accepts an injected immutable catalog, otherwise the existing configured DBC
path or three-warrior-stance fallback. An absent druid row does not invent a
display producer. Warrior stance power/boost behavior remains intact.

The selected form has exact holder ownership, writes the existing form byte and
notifies equip-form consumers. Competing holders are removed through the normal
SpellSystem aura lifecycle. Transform has visual priority; its scale replaces
the form overlay and preserves independent ModScale factors. Removing either
overlay in either order restores the remaining form or native display/scale.
Applying a form while transformed captures the native scale from the transform
state rather than treating transformed scale as a new base.

Source: pinned vmangos SpellAuras.cpp 2317-2477 and 2735-2773, with the existing
FormDisplayTable ID/scale contract. Game tests exercise actual aura application
for Alliance/Horde displays, scale restoration and both transform orders;
World tests use a real socket cast with an injected catalog and actual removal.

Verified display-model metadata is not available through this slice: form and
transform changes preserve existing bounding radius/reach, while existing
ModScale changes retain their established geometry behavior. Full model-derived
geometry, druid power/speed/boost mechanics and original-client acceptance
remain pending. This document supersedes the earlier transform slice's
changing-form limitation only for the display/object-scale behavior above.
