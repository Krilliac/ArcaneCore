# Area: operator and developer documentation (wave 5, lane docs-wiki)

Documentation that is checked against the code, so it cannot silently drift. The pages are indexed in [docs/README.md](../README.md).
Everything lives in `tests/ArcaneCore.World.Tests/Docs` (a test folder: no new project, no solution edit) and in `docs/reference` and `docs/guide`.

## Delivered

**Generated reference pages** (a test renders each page from the code, compares it with the committed file after line-ending normalisation, and fails with the regenerate command when it is stale;
a missing file or an unreadable repository root fails, it never skips):

| Page | Generated from |
|---|---|
| [reference/configuration.md](../reference/configuration.md) | every options class with a `SectionName` constant, plus the sections bound without one (see the exception table), walked by reflection; defaults are read from constructed instances, meanings from the `///` summaries in the source, reload behaviour from `WorldConfigKeys` |
| [reference/gm-commands.md](../reference/gm-commands.md) | the live command table (`ChatCommands.CreateTable`), with the development-only roots (`.reload`, `.hotcode`, `.hotmodule`) switched on and labelled; per node the retail level and the lowest stored account that reaches it |
| [reference/schema.md](../reference/schema.md) | `DataModules` and the three context schema definitions: version, owning module or inline step, what it adds, which characters modules clean up on character delete |
| [reference/exit-codes.md](../reference/exit-codes.md) | the exit-code classes of the daemons and the content importer (the `arcane-db` codes stay in the runbook table, which a data test already keeps complete) |

**Hand-written guides, mechanically fact-checked:** [installation](../guide/installation.md) (every key, path, tool verb, dev-runner flag, port and default it names is checked; its database example binds through `DatabaseOptions`),
[operations](../guide/operations.md) (its release-caveat register is asserted against the option defaults, so flipping a default in code fails until the register is reviewed), and
[contributing](../guide/contributing.md) (the build commands equal the CI workflow's run lines; the seams, variables and hygiene extension list it names are matched to the code).

**Guards over all documentation:** every relative link and heading anchor in every markdown file resolves (`DocsLinkTests`); every configuration key named in backticks in any page exists in the catalog, with an
allow-list of keys that are designed but not delivered, each with its reason (`DocKeyAuditTests`); a source scan finds every `GetSection`, `Configure<T>`, string-literal indexer and `ConfigKey` constant in `src` and `tools`
and requires it to be covered by the catalog or the exception table, so a lane that binds a new section without documenting it fails with the file and the key (`ConfigReferenceTests`).

**Drift fixed** (each was wrong at the lane base, and a test now ties the statement to the code): README and the M1 acceptance page told operators to enable `Auth:AutocreateAccounts` without saying it is off in the shipped
configuration and not retail; `docs/security/hardening.md` gave `WriterDrainGrace` as 5 s in one place and `00:00:00` (wait forever, the code default) in another, and listed the pre-auth deadline and the inbound queue bounds as
not delivered although they are; `docs/areas/quests-npc.md` said gossip, vendor and trainer behaviour does not exist; and the creature-AI and live-reload pages named keys without their section prefix, so an operator copying
an unprefixed `EventAi` key would have set a key nothing reads.

**Findings recorded by the catalog:** the three `World:Chat` flood keys are bound twice, by `ChatFeature` into `ChatOptions` and by `ChatRestrictionFeature` into `ChatRestrictionOptions` (same keys, same defaults, different
integer types); the `RewardRange` property of the loot options is computed, not a key; `World:Perf:*` is a dead alias of `PerformanceLog:*`. The generated page lists each under "Aliases and framework sections".

## Regenerating, and what the integrator must do

The golden pages are generated from the tree they were written on, so after other lanes add options, commands or schema steps the corresponding tests fail by design until the pages are regenerated:

```
ARCANECORE_UPDATE_DOCS=1 dotnet test tests/ArcaneCore.World.Tests --filter Docs
```

Review the resulting diff (it lists every option, command and schema step the merge added), then run the Docs tests without the variable. Also expect these to need a one-line fix after a merge: an options class bound without a
`SectionName` constant (a row in the exception table), an option with no `///` summary (write the summary next to the option; a documentation override only exists for the ones that predate this lane), a document that names a
key before its option exists (the allow-list, with a reason), a new `docs/reference` page that is not linked from the index, and a new non-retail default that belongs in the operations register. Merge this lane last, or in the same
integration commit as the regeneration, so the integration branch is not red between the two.

## Limits

- **No feature matrix.** A page that rates each area Complete, Partial, Inert or Planned is a human judgement over the area pages; it was not written here, because a status table that nobody has re-derived for the merged tree would
  claim more than is known. The area pages (each with its Delivered and limits sections) are the status source until one is written.
- **No vmangos command-coverage figures.** The command reference lists what ArcaneCore has and says it is a subset; a count of the vmangos table needs the reference clone, which hosted CI does not have, so no figure is asserted.
- **Retail citations in the guides are authored text.** The release-caveat register cites vmangos lines (verified in the reference clone when written); CI asserts only the ArcaneCore side of each statement.
- **The scan is a ratchet, not a proof.** It knows the binding styles in use; a key built at run time (interpolated, or held in a variable) is not seen. Environment-variable-only keys read through helpers are likewise invisible to it.
- **No provider behaviour is proved.** This lane reads schema metadata only and adds no store, so nothing here ran against MariaDB or PostgreSQL; the schema page says so and links the pages that state the provider semantics.
- **The installation walk-through has not been run on a clean machine** by an automated check, and no real 1.12.1 client has been recorded against it.
- There is no retail-configuration-key column and no importer of a `mangosd.conf`; where an option has a retail counterpart its summary names the key.

## Provenance and references

The retail statements in the operations guide were read in the vmangos clone: `World.cpp:556`, `:558` and `:582` (code defaults of `ListenRange.Say`, `ListenRange.TextEmote` and `GridUnload`),
`mangosd.conf.dist.in:399` (`GridUnload = 0`), `:1559-1561` (the shipped ranges), `:2536` (`GM.LowerSecurity = 0`), `realmd.conf.dist.in:208-209` (`WrongPass.MaxAttempts`, `WrongPass.ThrottleWindowDurationSec`),
`Chat.h:66-70` (`AllowConsole`), `Chat.cpp:1212` (the reload root, always registered, `SEC_DEVELOPER`) and `WorldSocket.cpp:621-628` (`Network.TimeoutSecsIfNoAuth`). Nothing from the references is copied into the repository.
