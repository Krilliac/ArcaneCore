# GM first-login tools (WoW 1.12.1, build 5875)

Add this opt-in block to appsettings.json under World:GmCommands:

```json
{
  "FirstLoginTools": {
    "Enabled": true,
    "MinimumSecurity": "GameMaster",
    "IncludeDeveloperSpells": true,
    "ShowToolGuide": true,
    "SpellIds": [],
    "ExcludedSpellIds": []
  }
}
```

The shipped default is disabled. Empty SpellIds selects the reviewed rank-filtered preset; a nonempty list narrows it. ExcludedSpellIds removes entries. Neither list can introduce an unaudited ID or bypass rank filtering. Each list is limited to64 unique reviewed IDs. Set IncludeDeveloperSpells=false for the support-only preset. Restart the World service after changing the configuration.

New means the character's existing first-login state, PlayedTime==0, including imported never-played rows. The first successful world save stores at leastone second, so later relogs do not restore deliberately unlearned tools. Existing played characters are unchanged. Failed first-login persistence prevents world entry; a retry reconciles the final spellbook.

The default minimum is GameMaster. That tier gets12 support tools, including the two detection utilities plus ten test/support records. Administrators also receive19 developer records, for31 total. If an operator deliberately sets MinimumSecurity=Moderator, moderators receive only the two detection entries. These are ArcaneCore operator tiers, not an assertion about Blizzard's internal staff permissions.

The catalog is verified against the supplied build5875 Spell.dbc and the loaded ID/name/nonpassive spell records. It includes test movement/binding, stealth/invisibility, size/lighting, healing, QA instant-cast, debug combat/control and GM-toggle records. Catalog availability does not independently prove every complex effect works in this core or that Blizzard used a given ID in a specific staff workflow. IDs11/2583 are omitted because modern emulator comments disagree with the actual5875 names;885/10228 are omitted duplicate higher-level invisibility variants.

Learning adds no auras, casts no spells, activates no GM mode or badge, changes no account level, and expands no talent/rank/trigger chains. The guide points to.commands/.help/.lookup spell; command authorization continues to use the account rank and configured command levels. Audit records use numeric character/security IDs, source build, configuration revision and per-spell Added/AlreadyKnown outcomes, without credentials/IP/rawDBC data.

Before activation require the first-login/rank/persistence/failure/relog tests plus the complete source/export gate. This document is a draft until that qualification succeeds.
