# Atlas client files (browser-side reader)

Atlas can draw the world from the visitor's own World of Warcraft 1.12.1 (build 5875) installation. The reading happens in the browser, in module Workers; the server never sees a file, a file name from the visitor's folder or a byte derived from it.

Code: `tools/ArcaneCore.Atlas/Web/js/clientfs/`. Tests: `tests/ArcaneCore.Atlas.Tests/Web/clientfs.test.mjs` (run by `ClientFsFixtureTests`).

## Integration status

The reader is reachable from the Local page (lane Y39). `js/clientsource.js` (`ClientFilesSource`) sits between the viewport and the host API: tile requests of layer `ref.terrain.tex` and texture keys starting `cft:` go to the reader when the host cannot answer them (the host answers NotApplicable, Missing or Rejected, or does not know the layer), every other request goes to the host untouched. The reader module (`clientfs/index.js`) is imported on first need, so a page that never asks loads none of it. The top bar has a **Client files** button that opens `openClientFilesSettings()` (Choose or Change folder, Forget folder, cache budget); with no folder chosen the button opens the first-entry dialog, and once a folder is chosen (or changed, or forgotten) the page asks again for the textured terrain tiles it holds. `app.js` counts the reader into the layer list (`withClientReaderLayers` in `catalog.js`): a host that reports `hasSource: false` for `ref.terrain.tex` still gets a usable checkbox, and ticking it requests tiles through `ClientFilesSource`. A textured tile requested before any folder was chosen answers NotApplicable without opening a dialog; the dialog and the Re-allow banner appear on a cache miss only when a folder choice already exists. A textured terrain tile is never served from the OPFS cache while no live folder exists (`needsLiveFolder` in `clientfs/index.js`): the baked textures are memory-only, so a cached tile alone would draw every chunk with the missing-texture checker. The request does not wait for the folder: it answers NotApplicable at once and opens the Re-allow banner or the dialog, so an ignored prompt cannot hold one of the Scene's tile-fetch slots; when the folder opens the reader's `onClientFilesChanged` fires and the viewport invalidates the layer and asks again. A declined prompt keeps NotApplicable, so nothing is drawn. Choosing, changing or forgetting the folder also replaces the texture store, so the old folder's baked textures never answer for the new one.

Still open:

- The public page (`js/public/`) draws a 2D chart and loads no terrain, so there is nothing for it to call `showFirstEntryDialogIfNeeded()` for; when it gets a textured view it uses `ClientFilesSource` like the Local page.
- The "module Worker under the public CSP" test uses a header the test harness assembles (`localCsp()` plus `worker-src 'self'; frame-ancestors 'none'`) because no public listener exists on this base. Re-run it against the real header when that listener lands.
- The terrain textures are verified on synthetic data only (see Terrain textures below); the orientation of the alpha maps against the layer textures needs a look at real 1.12.1 data before it is called accepted.

## What the visitor sees

- First entry: a dialog explains the feature and offers **Choose folder**, **Skip** and **Why?**. Skip is remembered; the map stays untextured. The Local page opens it from the top bar **Client files** button or on the first request that needs files (`showFirstEntryDialogIfNeeded()` is part of the reader's API but no page calls it on load).
- Choosing a folder validates it before anything is stored or cached. Errors are shown in the dialog (no Data folder, a Burning Crusade or later client, a missing or damaged archive, a different build). A `Data/<locale>` folder alone is a warning.
- Later visits: with a stored folder handle the browser needs one click, so a **Re-allow access** prompt appears; a denial keeps the prompt and says the browser blocked access.
- Settings: folder name, build, cache size, a cache budget slider (default `min(2 GiB, storage quota)`), **Change folder** and **Forget folder** (deletes the stored handle and the whole cache).

## Read scope

Only `Data/*.MPQ`, the names of Data's sub-folders (for the locale warning) and `WoW.exe` are opened. WTF, Interface, Logs and Screenshots are never opened or listed. `scope.js` is the only module that touches a directory handle or a `FileList`; a `webkitdirectory` FileList is filtered by relative path before any `File` is passed on.

## Moving parts

| Module | Role |
|---|---|
| `limits.js` | Parser limits and the typed `ClientFsError` codes. |
| `mpq.js`, `explode.js`, `dbc.js` | MPQ v0/v1 reader (patch chain in `MpqFileSystem` load order), PKWARE explode, DBC reader. bzip2 is refused. |
| `geometry.js`, `adt.js` | ADT terrain/liquid to ATG1, byte-identical to `ClientTerrainGeometry` + `GeometryWire`. |
| `blp.js`, `terrain-tex.js` | BLP2 decoder (port of `BlpDecoder.cs`); MTEX/MCLY/MCAL, baked terrain textures and the `ref.terrain.tex` tile. |
| `validate.js` | Folder checks and the PE version resource. |
| `scope.js` | Directory handle / FileList scan. |
| `store.js` | In-memory LRU (512 MB), OPFS cache with a byte budget, IndexedDB `atlas-client` (`handles`, `meta`). |
| `pool.js`, `worker.js` | Worker pool (`min(4, cores - 1)`), operations `ping`, `validate`, `tile`, `texture` and `model` (kinds `m2`, `m2-animation`, `wmo-root`, `wmo-group`), 5 s per file and 30 s per terrain tile (a baked texture counts as a tile job); a timed-out Worker is terminated, replaced and the key marked bad. |
| `dialog.js`, `settings.js`, `index.js` | UI and the public API: `showFirstEntryDialogIfNeeded`, `openClientFilesDialog`, `openClientFilesSettings`, `clientFilesStatus`, `localTileProvider`, `localTextureProvider`, `localModelProvider`, `onClientFilesChanged`. |

`localTileProvider().tile(layer, map, x, y, signal)` returns the same `{state, detail, body}` as `Api.tile`. Cache entries are keyed by logical path plus the name, size and last-modified time of every archive, so a changed archive misses. Without File System Access (Firefox, Safari) the folder comes from `<input webkitdirectory>`; `File` objects do not survive a reload, so only cached tiles load afterwards and a cache miss asks for the folder again.

## Security

Workers are started from `/app/js/clientfs/worker.js` (same origin), which the public listener's `worker-src 'self'` and Local's `script-src 'self'` allow. No client byte is committed to the repository; the tests build synthetic archives and run a canary detector over every request, header, body and WebSocket frame.

Not verified against real client data in CI (the real-data tests run only with `ARCANECORE_TEST_CLIENT_DIR`): the strict archive-size check, the compression-mask census and the bzip2 refusal on real archives.

## Terrain textures and BLP

- `blp.js` is the port of `Textures/BlpDecoder.cs`: BLP2 type 1 (palettized with 0, 1, 4 or 8 bit alpha, DXT1, DXT3, DXT5, raw BGRA) to straight-alpha RGBA8 with the whole mip chain, listed levels used while valid, the rest generated by the same alpha-weighted box filter, and the same limits (1 to 4096 pixels a side, 64 MiB decoded). It is written to give the C# decoder's output and is checked on synthetic files of every encoding, not byte-compared with the C# decoder; every failure is a `bad-file` error.
- `terrain-tex.js` reads MTEX, MCLY and MCAL, blends the layers of each MCNK on the CPU into a 128x128 RGBA texture (base layer, then each further layer by its 4-bit alpha map, every layer tiled 8 times across the chunk, mip level chosen so a repeat is at most a 2:1 minification) and builds the `ref.terrain.tex` tile: ATG1 version 2 with one batch and one `terrain` material per MCNK, UVs 0 to 1 across the chunk, no vertex shared between MCNKs. The baked texture of an MCNK is requested as `cft:<map>/<x>/<y>/<chunk>`, the Worker's `texture` operation returns it as an ATX1 texture, and `localTextureProvider().texture(key)` caches it in memory (96 MiB, never in OPFS). A layer whose BLP is missing or damaged draws a flat fallback colour; an MCNK with more than four layers keeps four; a layer flagged as compressed alpha (not present in 1.12.1 data) is skipped. The last two are counted in the tile's stats (`skippedLayers`).
- Shadow maps (MCSH), the specular and height layers of later clients and texture animation are not read.

## Deferred deliverables

Still not in the browser reader, with the reason:

| Deliverable | Reason | Owner |
|---|---|---|
| renderer bone streams and animation playback | The animation tables are decoded (`parseM2Animation`, below), but the renderer's vertex layout has no boneIndices/boneWeights streams and nothing samples the tracks, so `skinned` and `character` chunks draw in their bind pose. The decoder was written from the public pre-Wrath layout (wowdev.wiki/M2, pywowlib `m2_format.py`), not from a real client file | none assigned; lane Y40 covers attached item models only and was not extended |
| `js/assets/source.js`, `visitor.js`, `server.js` and the `assets.source()` loader lines | Dropped by the lane as replaced by `clientsource.js`. That is the lane's own judgement and is **not settled**: `clientsource.js` serves only `ref.terrain.tex` tiles and `cft:` textures, not a general asset source for models and item textures, and no design text for `assets.source()` was found in `docs/` or `_land/` | put to Nathan: confirm the drop or name the lane that builds the general asset source |

Animation tables (`parseM2Animation`, kind `m2-animation`): global sequences, the 68-byte sequence table and the bone tree with translation, rotation (four floats) and scale tracks in the pre-Wrath flat layout (u16 interpolation type, i16 global sequence, interpolation ranges, timestamps, values; 28 bytes per track). The two public sources agree on this and differ only on the 4-byte bone name CRC (wiki: from TBC; pywowlib: every version), so the reader tries 112-byte bone records (with CRC) and falls back to 108 bytes when that does not fit the file. Checked on synthetic files only; UNVERIFIED on real 1.12.1 data: the CRC presence, how the `ranges` map to sequences, the rotation component order and the tangent layout of hermite/bezier tracks.

No browser page calls `localModelProvider()` yet, so `m2.js` and `wmo.js` run on no page; only terrain uses the reader end to end.

Skipped with reason: the baked-texture provider ignores `maxSize` (the ATX1 bytes are not downscaled); the MCNK `do_not_fix_alpha_map` seam needs real data to see; the textured tile builder still records an edge set it does not need. None changes a result a visitor can see.

Shipped: MPQ, explode, DBC, ADT heights/liquid, BLP2, terrain textures, `clientfs/m2.js` and `clientfs/wmo.js` (ports of `M2ModelFile`, `WmoRootFile`, `WmoGroupFile`, read through the Worker operation `model` and `localModelProvider()`; no browser draw path consumes them yet), validation, the dialog and settings, and the Local page wiring.

## Declining

Skip, Not now and closing the dialog are remembered for the page session: later cache misses resolve `NotApplicable` without showing the modal or banner. An explicit `openClientFilesDialog()` (or a successful pick/re-allow) clears the decline. A textured tile with no open folder is answered `NotApplicable` at once while the dialog or Re-allow banner opens (it never waits on the visitor, so it cannot hold one of the page's tile-fetch slots); `onClientFilesChanged` fires when a folder was opened or forgotten and the page asks its textured tiles again. A request whose `AbortSignal` fires while it waits for files or for a Worker rejects immediately.
