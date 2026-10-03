# Codex security review: persistence / I/O findings

Source: an independent Codex (gpt-6-sol) review of main at 49448fd (area `persistence-io`). Each
finding was re-verified against the code before fixing. Defaults are behaviour-preserving for valid
input; nothing here adds a config switch because no valid input changes meaning.

## AC-PI-001 (high): WMO liquid grid size overflow

`WmoLiquid.Read` computed `((long)tilesX+1) * ((long)tilesY+1) * 4 + flags` and compared it with
the bytes remaining. For `TilesX=0x40000000, TilesY=0x80000000` the product wrapped negative, the
guard was bypassed and `new float[heightCount]` requested ~8 EiB. `VMapManager.AcquireModel`
catches only `InvalidDataException`, so the exception escaped the loader.

Fix (`VMaps/WorldModel.cs`): the counts are compared with what the LIQU chunk can hold
(`min(remaining, chunkEnd - position) / 4`) before any multiplication; the chunk end now bounds the
read (previously a liquid larger than its declared chunk was read past it and then `Seek` rewound).
Every malformed size throws `InvalidDataException`.

Evidence: `WmoLiquidHardeningTests`. RED before the fix: the 0x40000000 x 0x80000000 case threw
`OverflowException`, the 0xFFFFFFFF x 0xFFFFFFFF case and the "liquid larger than its chunk" case
threw nothing. GREEN after: all rejected with `InvalidDataException`, under 1 MB allocated.

## AC-PI-002 (medium, worse than reported): MySqlDumpReader column-list spin

In `ReadInsert`, when the next character after a column was not `)`, `,` or a word character
(EOF, `;`, a quote, any punctuation) `ReadWord()` returned `""` without consuming input and
`list.Add` repeated forever: a CPU spin with unbounded memory growth, not just a hang.

Fix (`World/Creatures/MySqlDumpReader.cs`): EOF and empty identifiers (including an empty quoted
name and an empty table name) throw `FormatException`; every iteration must consume input. New
`MySqlDumpLimits` (constructor argument, defaults far above real dumps): `MaxColumns` 4096 (the
MySQL limit, also applied to values per tuple), `MaxTokenChars` 16 Mi, `MaxStatementChars` 16 Mi.
Statements the reader skips (anything but CREATE TABLE) are no longer buffered at all, so a
large skipped statement costs no memory. A `CREATE TABLE` column with an unterminated backtick
now raises `FormatException` (a defensive guard; not reachable through the public API).

Evidence: `MySqlDumpReaderHardeningTests`. The test input is wrapped in a reader that fails when
Peek is called 20,000 times without a Read (so RED is a deterministic failure, not a hang). RED: 12
of 13 failed with "parser is spinning". GREEN: all pass; the ceilings each have a test.

## AC-PI-003 (medium): account password on the command line

`arcane-account create|set-password <user> <password>` exposed the secret in process listings,
shell history and logs.

Fix (`tools/ArcaneCore.AccountTool/PasswordSource.cs`, `Program.cs`): the password now comes from
`--password-stdin`, the `ARCANE_ACCOUNT_PASSWORD` environment variable, piped stdin, or a no-echo
prompt with confirmation when stdin is a terminal. The legacy argv form still works and prints a
warning on stderr. README and `docs/integration/quest-client-acceptance.md` use the stdin form.
Precedence: `--password-stdin`, argv (legacy), environment, piped stdin, prompt.

Evidence: `AccountToolPasswordSourceTests` (selection logic is a pure function). The API did not
exist before, so RED was the test project failing to compile against it; GREEN is 10 tests passing.
The interactive prompt itself (`Console.ReadKey`) is not unit-tested.

## Sweep

Same bug classes (size/count multiplied or added before comparison; parser loops that do not
consume; exception types escaping an `InvalidDataException`-only handler) over CollisionDataReader
and its callers, MMaps, VMapTree, TerrainTile/TerrainManager, DbcFile and the DBC readers, the
dump importers, and the packet/size allocations in World/Realm.

Method: besides reading, `MalformedFileSweepTests` feeds every collision/terrain reader a valid
file with every byte and every 4-byte window overwritten by extreme values (0, 0x7FFFFFFF,
0x80000000, 0x40000000, 0xFFFFFFFF...) and every truncation, and fails on any exception type
other than `InvalidDataException`.

Fixed:

1. `TerrainTile` section reader (`checked((int)offset)`): a header offset above `int.MaxValue`
   raised `OverflowException`, which `Parse` (catches `ArgumentOutOfRangeException` only) and
   `TerrainManager.LoadTile` (catches `InvalidDataException` only) let escape. Now reported as
   a truncated section. RED: the sweep found 40 escapes; targeted theory added. Exploitable by a
   crafted `.map` file.
2. `DbcFile.Parse`: `recordSize != fields * 4` was evaluated in `uint`, so `fields=0x40000001,
   recordSize=4` (or `fields=0x40000000, recordSize=0`) passed, producing a file whose field
   offsets overflow `int` and (with records > int.MaxValue) a negative `RecordCount`. Now 64-bit
   and `fields <= int.MaxValue / 4`. RED: 3 of 6 tests failed before; all pass after.

Reviewed, not exploitable (no change):

- `CollisionDataReader.ReadCount` already uses `long` multiplication; `ReadBytes` is checked.
- `GroupModel`/`BihTree.Read`/`VMapTree.ParseTile`/`VMapTree.Parse`: counts go through `ReadCount`
  (bounded by remaining bytes); `List<T>(count)` capacity is bounded by file size; triangle indices
  are validated in the `GroupModel` constructor; spawn tree slots are validated by
  `VMapTree.IsValidSlot` before use; BIH traversal is bounds-checked. The sweep confirms.
- `ModelSpawn.Read`: name length capped at `VMapFormat.MaxNameLength`.
- `NavMeshTile.Parse`: all counts are range-checked (`<= 1<<20`) and the size equation is done in
  `long` and must equal the data length exactly; polygon vertex indices and counts are validated.
  Off-mesh polygons with zero vertices (which would break `GetPolyHeight`/`Center`) are never
  returned by `Passes`, which every query path applies, so they are unreachable.
- `NavMeshParams.Parse`/`MmapTileHeader`: fixed size / range checked.
- DBC readers: `List<T>(RecordCount)` is bounded by file size after the `DbcFile` fix. (Several readers
  also catch `ArgumentException` around row construction; not audited further.)
- `CreatureDumpImporter`/`GameObjectLootDumpImporter`: admin-side import tools over
  `MySqlDumpReader`; with AC-PI-002 their input is now bounded. They are not on a network path.
- Wire allocations: `AddonInfo.TryDecompress` (size capped at 0xFFFFF), `AccountDataCompression`
  (`MaxDecompressedSize`, size compared), `WorldSession` payload (`MaxClientPacketSize`),
  `ChatHandlers` who-list counts (capped 10 / 4), taxi express nodes (count capped before
  `count * 4`), `LogonSession` (u16 body length). All bounded before allocation.
- Parser loops: the only non-consuming loop was the AC-PI-002 column list. Other
  `MySqlDumpReader` loops (`Read`, `ReadTuple`, `ReadStatement`, `SplitTopLevel`) consume on every
  iteration; `VMapTree.Parse`'s `while (!AtEnd)` consumes at least one spawn record per pass.
- Handlers that catch only `InvalidDataException` (`VMapManager` x3, `NavMeshPathfinder` x2,
  `TerrainManager`): after fixes 1 and AC-PI-001 no reader escapes with another type (sweep). The
  handlers were not broadened, so a future regression shows up as a test failure rather than a
  silently swallowed error.
