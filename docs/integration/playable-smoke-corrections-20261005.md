# Populated-world smoke corrections

An explicit `arcane-mock live --character NAME` previously selected the first existing character when NAME was absent. That made a fresh-character request appear to pass without exercising creation. Explicit requests now select an exact case-insensitive name or create and re-enumerate that name. Omitted character selection retains the existing fallback.

Normal server logout can wait twenty seconds before completion. The live runner must accept either successful immediate or delayed responses, bound the entire logout stage, wait for traffic before each bounded packet read, and require an empty completion packet. Failed or timed-out logout must produce a failing exit status. The synthetic scenario runner keeps its separate immediate-logout contract.

Starting outfit quantities follow pinned CMaNGOS `Player::Create` and its item-template `Spells[0].SpellCategory`: category 11 uses four food items when stack size exceeds four; category 59 uses two drinks when stack size exceeds two; otherwise BuyCount applies. Spell.dbc's separate Category is not substituted. Private staging quantities are audited separately from source qualification.

The first complete correction gate exposed an intermittent mail-deletion refusal. A held-read regression then confirmed that an older login-time mailbox read could replace a newer refresh with its empty snapshot. Mailbox loads must track the latest read generation independently of the commit revision, so an older completion cannot replace newer data. Existing commit-revision reloads, mailbox ownership, escrow integrity and COD refusal remain required. The regression checks cached letters and durable attachment deletion together.

Source references: mangos-classic `8ec338a1704e7dcb1c0213eb7ed58f9231ade40f`, `src/game/Entities/Player.cpp`, creation outfit loop; ArcaneCore `ProtocolIO.OperationTimeout`, `WorldClient.WaitForTrafficAsync`, and the existing normal logout handler. Client assets, account secrets, and populated databases remain local external inputs.

Local source qualification: 14,783 passed, six existing skips, zero failures; focus19/native59/clean Release. Frozen-source and export checks verified. Populated-world gameplay, casts, quests, terrain, random spawn candidates, and NPC template policy require separate evidence.
