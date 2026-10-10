# Server mail

`ServerMailFeature` (src/ArcaneCore.World/ServerMail/ServerMailFeature.cs) ports AzerothCore's server mail: ServerMailMgr
(src/server/game/Mails/ServerMailMgr.cpp) and the login script ServerMailReward::OnPlayerLogin (src/server/scripts/World/server_mail.cpp).
It is on by default (`ServerMail:Enabled`, [configuration reference](../reference/configuration.md#servermail)); with empty tables
nothing is sent.

## Delivered

- **Tables (characters schema 54, `ServerMailDataModule`).** `mail_server_template`, `mail_server_template_items`,
  `mail_server_template_conditions` and `mail_server_character`, with AzerothCore's columns. The templates load at start
  (`ServerMailRules.Load`); a bad row is skipped with a logged message, as AzerothCore does.
- **Conditions.** Level, PlayTime, Quest, Reputation, Faction, Race and Class follow ServerMailCondition::CheckCondition.
  Achievement and AccountFlags do not exist in 1.12 and are not loaded.
- **At login.** Every active template the character has not had yet and whose every condition it meets is mailed to it. A template
  whose conditions fail stays owed for a later login. The letters only go out while the same character is still in the world.
- **Letters.** `EconomyFeature.SendServerMail` builds the letters: one item per letter (the 1.12 client shows one), the faction's
  money on the first, the same subject and body on each. A creature sender uses MAIL_CREATURE with default stationery; otherwise the
  letter comes from the receiver with GM stationery, as AzerothCore does. Items are created new, straight into escrow under their
  letter (`CreateEscrowItem`).
- **Exactly once per (template, character).** The template's `mail_server_character` row is written by the same economy
  transaction as its letters and their items (`RecordServerMailSent`). The store refuses the whole transaction when the row already
  exists, and the table's (guid, mailId) primary key backs that check against a concurrent writer. So:
  - every letter of a template commits together or not at all (no partial reward);
  - a crash, a lost acknowledgement or an unreadable outcome after the commit leaves the row in place, and the next login does not
    send the template again;
  - a commit that fails leaves no row, and the template is sent at the next login;
  - two logins that both read the template as owed (a quick relog) start two transactions, and only one commits.

  AzerothCore writes `mail_server_character` in a separate statement after the letters; ArcaneCore does not, because a failure or a
  restart between the two would send the reward twice (item and money duplication). No separate sent-record write exists.
- **Character deletion** drops the character's `mail_server_character` rows.

## Limits

- The quest catalogue is not reachable from the loader: an unknown quest id in a Quest condition is not rejected at load; it simply
  never matches.
- The economy store must be enabled (`IEconomyStore` registered) for any letter to be sent; without it the template stays owed.

## Tests

- `tests/ArcaneCore.World.Tests/ServerMail/ServerMailTests.cs`: loading, conditions, owed letters, auto broadcast.
- `tests/ArcaneCore.World.Tests/ServerMail/ServerMailDeliveryTests.cs`: delivery on a real store across a crash after the commit,
  a failed commit and retry, a relog during an in-flight delivery, a multi-item template, and a request without a template id
  (refused).
- `tests/ArcaneCore.Data.Tests/EconomyStoreTests.cs` (`RecordServerMailSent_*`) and `ServerMailStoreTests.cs`: the record commits
  with every letter, a second record refuses the whole operation, a refused letter leaves no record, a record written by a
  concurrent delivery after the existence check fails the save on the key and rolls everything back, and the key is unique.
  These run on every configured provider; MariaDB and PostgreSQL only when their connection variables are set.
