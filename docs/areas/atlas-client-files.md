# Atlas client files (browser-side reader)

Atlas can draw the world from the visitor's own World of Warcraft 1.12.1 (build 5875) installation. The reading happens in the browser, in module Workers; the server never sees a file, a file name from the visitor's folder or a byte derived from it.

Code: `tools/ArcaneCore.Atlas/Web/js/clientfs/`. Tests: `tests/ArcaneCore.Atlas.Tests/Web/clientfs.test.mjs` (run by `ClientFsFixtureTests`).

## What the visitor sees

- First entry: a dialog explains the feature and offers **Choose folder**, **Skip** and **Why?**. Skip is remembered; the map stays untextured.
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
| `validate.js` | Folder checks and the PE version resource. |
| `scope.js` | Directory handle / FileList scan. |
| `store.js` | In-memory LRU (512 MB), OPFS cache with a byte budget, IndexedDB `atlas-client` (`handles`, `meta`). |
| `pool.js`, `worker.js` | Worker pool (`min(4, cores - 1)`), 5 s per file and 30 s per terrain tile; a timed-out Worker is terminated, replaced and the key marked bad. |
| `dialog.js`, `settings.js`, `index.js` | UI and the public API: `showFirstEntryDialogIfNeeded`, `openClientFilesDialog`, `openClientFilesSettings`, `clientFilesStatus`, `localTileProvider`. |

`localTileProvider().tile(layer, map, x, y, signal)` returns the same `{state, detail, body}` as `Api.tile`. Cache entries are keyed by logical path plus the name, size and last-modified time of every archive, so a changed archive misses. Without File System Access (Firefox, Safari) the folder comes from `<input webkitdirectory>`; `File` objects do not survive a reload, so only cached tiles load afterwards and a cache miss asks for the folder again.

## Security

Workers are started from `/app/js/clientfs/worker.js` (same origin), which the public listener's `worker-src 'self'` and Local's `script-src 'self'` allow. No client byte is committed to the repository; the tests build synthetic archives and run a canary detector over every request, header, body and WebSocket frame.

Not verified against real client data in CI (the real-data tests run only with `ARCANECORE_TEST_CLIENT_DIR`): the strict archive-size check, the compression-mask census and the bzip2 refusal on real archives.
