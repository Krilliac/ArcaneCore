# Wave 4 lane: optional client data (2026-10-08)

Branch `claude/w4-optional-dbcs` from `08ea5ff6`. Closes the wave-3 "Data still needed" line "Optional DBCs and dumps the world still
logs as missing: ItemSets, ItemRandomProperties, PageText, SpellItemEnchantment, CharSections". No schema change.

## What changed

* **Data.** The five client files were extracted read-only from `D:\World of Warcraft Classic 1.12.1\Data` with the same mpqcli v0.11.0
  into `D:\refs\client-dbc-5875-effective` (patch-2 over patch over dbc; listed in its `SHA256SUMS` and `README.txt`): ItemSet.dbc and
  SpellItemEnchantment.dbc from patch-2.MPQ, ItemRandomProperties.dbc, CharSections.dbc and CharacterFacialHairStyles.dbc from patch.MPQ.
  The patch-2 ItemSet.dbc differs from the patch.MPQ copy; the live profile's `DBFilesClient-5875` copies of ItemRandomProperties and
  SpellItemEnchantment are byte-identical to the effective ones. `page_text` and `item_enchantment_template` stay in the classic-db dump,
  which the world reads at start (about 1 s each).
* **Character appearance** (the only code that did not exist): vmangos `Player::ValidateAppearance` over CharSections.dbc and
  CharacterFacialHairStyles.dbc, keys `CharacterCreation:CharSectionsDbcPath` and `CharacterFacialHairStylesDbcPath`
  (docs/areas/character-creation.md "Appearance"). The stale startup warning that also claimed the starting outfit and action bar were
  not enforced (both were delivered in wave 2) is gone.
* **Tooling.** `tools/content/set-optional-data.ps1` checks the files and writes the seven keys; `refresh-world-content.ps1 -AppSettings`
  runs it after the content refresh, so the live refresh stays one command (docs/areas/items.md "Optional client data").

## Proof on a copy of the live profile

`D:\ArcaneCore-lanes\_logs\w4-optional-dbcs\`: the live `appsettings.json` and the three databases (SQLite backup API from read-only
connections) copied to `profile-copy\`, ports moved to 28471/23871, Realm and World from this branch's Release build.

* `copy-red\world.log` (live keys only): the five warnings (ItemSets, ItemRandomProperties, PageText, Enchanting, CharacterCreation).
* `set-optional-data.ps1` dry run, run, second run ("every key is already set"); the rewritten file equals the old one plus the seven keys.
  The refresh with `-AppSettings` on the copy's world database produced the same file (`refresh-with-appsettings.log`). Bad inputs are
  refused before anything is written: a dump without `page_text`, a DBC that does not match `SHA256SUMS`, a wrong field count, a missing file.
* `copy-green\world.log`: none of the five warnings; "appearance is checked against 3603 CharSections and 136
  CharacterFacialHairStyles rows", "Enchanting: 1460 enchantments", "Loaded 172 item sets", "Loaded 2012 random properties and 772
  item_enchantment_template entries", "Loaded 1427 page texts".
* A GM mock-client session on the copy created a character (all-zero looks, appearance check on) and `.additem 14091`, `14094`, `14091`
  (Beaded Robe/Wraps, `random_property` 454): the saved `item_instance` rows carry `random_property_id` 839 "of the Eagle", 1009 "of the
  Whale", 1009, with enchantments 79/71 and 82/71 in slots 3-4 (`copy-green\items-check.txt`). Before this, none of the live realm's 712
  items had a random property.

In-process against the real files (`OptionalClientDataRealTests`, `ARCANECORE_TEST_DBC_DIR` + `ARCANECORE_CLASSICDB_DUMP`): no warning,
set 209 "Battlegear of Might" with 3/5/8-piece bonuses 23562/21838/23561, a suffix roll whose enchantments the enchantment catalog
names, page 15 ("Hello Morgan,") over CMSG_PAGE_TEXT_QUERY, and character creation refusing hair style 40 and a bearded human woman while
accepting hair 11, colour 9, beard 8.

Not proven here: a set bonus cast from real Spell.dbc data on equip (the set decode and the synthetic equip tests are), and nothing ran
against a real 1.12.1 client.

## Live deploy

The content refresh step gains one argument; then the World is restarted as usual:

```
powershell -NoProfile -File <deploy>\tools\content\refresh-world-content.ps1 -WorldDatabase ...\complete-r5\world.db
  -DbcDirectory D:\refs\client-dbc-5875-effective -Importer <deploy>\bin\ArcaneCore.ContentImporter\release\arcane-content-importer.dll
  -BackupDirectory <out>\content-refresh-backups -Report <out>\04-refresh-report.json
  -AppSettings C:\Users\Nathan\Documents\Codex\2026-10-04\c\work\ArcaneCore-playable-standard-20261005-r3\appsettings.json
```

or, on its own with the World stopped, `set-optional-data.ps1 -AppSettings <profile>\appsettings.json -DbcDirectory
D:\refs\client-dbc-5875-effective`. Expected: five "matches SHA256SUMS" lines, the dump line, seven key lines, the backup line, then after
the restart none of the five warnings in `world.log`. Rollback: copy `appsettings.json.<stamp>.bak` back and restart the World.
