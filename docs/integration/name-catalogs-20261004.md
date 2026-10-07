# Immutable name catalog producers

The optional `Names` world section accepts paired `NamesProfanityDbcPath` and
`NamesReservedDbcPath` paths. Configuring neither leaves the immutable catalog
empty; configuring only one fails startup. Files use the build-5875 WDBC `ds`
layout: two four-byte fields per row, hence an 8-byte fixed record, documented
by vmangos `DBCStructure.h:480-489` and `DBCfmt.h:64-65`.

Catalog rows compile case-insensitive, culture-invariant, non-backtracking
regexes with a finite timeout. Only vmangos' documented Emacs `\\^` and `\\$`
anchor translations are applied; unsupported regex constructs and malformed
WDBC/string offsets fail closed. Each loaded source records its full path,
SHA-256 and row count for provenance. `NameCatalog.Check` returns
`Allowed`, `Profane`, or `Reserved`. The world-scoped immutable service feeds
character creation after name syntax checks and before database name lookup,
returning the existing profanity/reserved result codes. Pet naming composes
the catalog with its existing injected veto. Strict UTF-8 decoding requires a
NUL terminator and valid string offset; literal U+FFFD is accepted as valid UTF-8.

No original catalog binaries are present in the bounded known fixture/reference
roots, so synthetic WDBC tests prove parser behavior only. Original catalog
acceptance remains pending developer-supplied build-5875 files. The SQL
`reserved_name` loader is outside this tranche and requires a separate data
ownership decision.
