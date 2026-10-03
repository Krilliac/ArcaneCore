# Integration notes: social (friends/ignore, groups/raids, guilds, chat channels)

Branch `feat/social`, branched from `claude/friendly-hamilton-cuz4j4` after the seam PR #1
(merge 70bf59b) and built only on its seams.

## Schema version

| Database | Version | Module | Change |
|---|---|---|---|
| characters | **6** | `ArcaneCore.Data.Social.SocialDataModule` | new tables `character_social`, `guild`, `guild_rank`, `guild_member` (additive only) |

Assigned in the [2026-10-03 integration candidate](fleet-20261003.md), after inventory,
spellbooks and quest state. The source branch originally requested characters v3.

## Seams used (no shared registration files edited)

| Seam | Implementation |
|---|---|
| `IOpcodeHandlerGroup` | `World/Social/SocialHandlers.cs`, `GroupHandlers.cs`, `GuildHandlers.cs`, `ChannelHandlers.cs` |
| `IWorldFeature` + `IChatMessageHandler` | `World/Social/SocialFeature.cs` (party/raid/raid leader/raid warning, guild/officer, channel chat) |
| `WorldRuntime.PlayerLoggedIn` / `PlayerLoggingOut` | `SocialFeature.OnLoggedIn` / `OnLoggingOut` |
| `ICommandGroup` | `World/Social/GuildCommands.cs` (`.guild create/invite/uninvite/rank/delete`) |
| `IDataModule` | `Data/Social/SocialDataModule.cs` (+ `EfSocialStore`, scoped `ISocialStore`) |
| `IWorldTestServices` | `tests/ArcaneCore.World.Tests/Social/SocialTestServices.cs` (in-memory `ISocialStore`) |

## Shared-file edits

| File | Edit | Why |
|---|---|---|
| `src/ArcaneCore.World/Characters/CharacterDirectory.cs` | additive `FindByName(string)` (case-insensitive linear scan) | friend/guild requests name offline characters (vmangos GetPlayerGuidByName over its player cache) |
| `tests/ArcaneCore.Data.Tests/M6StoreTests.cs` | the characters-upgrade assert compares with `CharacterDbContext.Schema.CurrentVersion` instead of the literal `2` | any characters module (v3 here) raises the current version; the auth assert is unchanged |

Not edited: `WorldServiceCollectionExtensions.cs`, `ChatHandlers.cs`, `CharacterHandlers.cs`,
`WorldRuntime.cs`, the DbContexts, `WorldTestHost.cs`, `LoginSequence.cs`.

## Behaviour other areas should know

- `LoginSequence` still sends empty friend/ignore lists. `SocialFeature` loads the stored list
  after `PlayerLoggedIn` and resends the lists only when they are non-empty (so a character
  with no social entries sees no extra packets).
- On logout the feature leaves every channel silently, drops pending group/guild invites and
  marks the member offline in its group. Group membership is kept in memory (not persisted).
- `PLAYER_GUILDID` / `PLAYER_GUILDRANK` are set by `GuildManager` at login and on every
  membership/rank change. `/who` (in `ChatHandlers.cs`) still reports no guild name; a
  follow-up can read `SocialFeature.Context.Guilds.GetGuildOf(player)?.Name`.
- Character deletion has no hook: social rows and guild memberships of deleted characters are
  skipped when loaded (friend lists, guild load) rather than deleted.
- Cross-faction options (`SocialFeature.Options`: AllowTwoSide AddFriend/Group/Guild/Channel)
  default to off and are not bound to configuration yet.
- Social writes go through an ordered background queue (`SocialWriteQueue`); the feature
  drains it when the service provider disposes it at shutdown.
