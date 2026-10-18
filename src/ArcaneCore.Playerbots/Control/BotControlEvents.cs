using System.Globalization;
using System.Text;
using ArcaneCore.Game;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots.Scenarios;

namespace ArcaneCore.World.Playerbots.Control;

/// <summary>
/// What a controlled bot's server packets mean, as <c>.botctl</c> events (docs/ops/botctl.md, "Events"): a short kind, a one-line
/// text and whether it is an error (refused casts, attacks, purchases, quests ...: the "last errors" of <c>.botctl errors</c>). High-volume
/// packets (object updates, movement, combat logs of others) are dropped before they are queued (<see cref="IsNoise"/>). Every decoder is
/// guarded: a body it cannot read becomes a generic event with the opcode name. Pure; any thread.
/// </summary>
internal static class BotControlEvents
{
    internal readonly record struct Described(string Kind, string Text, bool Error);

    private static readonly HashSet<WorldOpcode> Noise =
    [
        WorldOpcode.SmsgUpdateObject, WorldOpcode.SmsgCompressedUpdateObject, WorldOpcode.SmsgDestroyObject, WorldOpcode.SmsgMonsterMove,
        WorldOpcode.SmsgSpellnonmeleedamagelog, WorldOpcode.SmsgPeriodicauralog, WorldOpcode.SmsgUpdateAuraDuration,
        WorldOpcode.SmsgSpellStart, WorldOpcode.SmsgPong, WorldOpcode.SmsgSetProficiency, WorldOpcode.SmsgInitWorldStates,
        WorldOpcode.SmsgUpdateWorldState, WorldOpcode.SmsgPlaySound, WorldOpcode.SmsgGameobjectCustomAnim, WorldOpcode.SmsgEmote,
        WorldOpcode.SmsgAiReaction, WorldOpcode.SmsgQuestgiverStatus, WorldOpcode.SmsgSpelllogmiss, WorldOpcode.SmsgSpellenergizelog,
        WorldOpcode.SmsgSpellheallog, WorldOpcode.SmsgEnvironmentaldamagelog, WorldOpcode.SmsgSpellCooldown, WorldOpcode.SmsgCooldownEvent,
        WorldOpcode.SmsgTextEmote, WorldOpcode.SmsgSpellDelayed, WorldOpcode.SmsgStandstateUpdate, WorldOpcode.SmsgLoginVerifyWorld,
    ];

    /// <summary>Whether <paramref name="opcode"/> is never recorded (cheap: called under the session's send lock).</summary>
    internal static bool IsNoise(WorldOpcode opcode)
    {
        if (Noise.Contains(opcode)) return true;
        string name = WorldOpcodeNames.GetName(opcode);
        return name.StartsWith("MSG_MOVE_", StringComparison.Ordinal) || name.StartsWith("SMSG_FORCE_", StringComparison.Ordinal)
            || name.EndsWith("_QUERY_RESPONSE", StringComparison.Ordinal) || name.StartsWith("SMSG_SPLINE_", StringComparison.Ordinal);
    }

    /// <summary>The event for one packet, or null to skip it (another unit's business). <paramref name="self"/> is the bot's guid.</summary>
    internal static Described? Describe(WorldOpcode opcode, byte[] payload, ulong self)
    {
        try
        {
            return Decode(opcode, payload, self);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or IndexOutOfRangeException or InvalidOperationException or EndOfStreamException)
        {
            return Generic(opcode, payload);
        }
    }

    private static Described Generic(WorldOpcode opcode, byte[] payload)
        => new(Kind(opcode), payload.Length.ToString(CultureInfo.InvariantCulture) + " bytes", false);

    private static string Kind(WorldOpcode opcode)
    {
        string name = WorldOpcodeNames.GetName(opcode);
        if (name.StartsWith("SMSG_", StringComparison.Ordinal)) name = name[5..];
        return name.ToLowerInvariant().Replace('_', '-');
    }

    private static string I(FormattableString text) => FormattableString.Invariant(text);

    private static Described? Decode(WorldOpcode opcode, byte[] payload, ulong self)
    {
        switch (opcode)
        {
            case WorldOpcode.SmsgCastResult:
            {
                CastResultView cast = ScenarioDecoders.CastResult(payload);
                return cast.Success
                    ? new("cast-ok", I($"spell {cast.SpellId}"), false)
                    : new("cast-failed", I($"spell {cast.SpellId}: {cast.Reason}"), true);
            }
            case WorldOpcode.SmsgSpellFailure:
            {
                SpellFailureView failure = ScenarioDecoders.SpellFailure(payload);
                return failure.Caster != self ? null : new("spell-failure", I($"spell {failure.SpellId}: {failure.Reason}"), true);
            }
            case WorldOpcode.SmsgSpellGo:
            {
                SpellGoView go = ScenarioDecoders.SpellGo(payload);
                return go.Caster != self ? null : new("spell-go", I($"spell {go.SpellId} hit {go.Hits.Count}"), false);
            }
            case WorldOpcode.SmsgAttackerstateupdate:
            {
                AttackerStateView hit = ScenarioDecoders.AttackerState(payload);
                if (hit.Attacker == self) return new("melee-dealt", I($"{hit.TotalDamage} to {Guid(hit.Victim)} ({hit.VictimState})"), false);
                if (hit.Victim == self) return new("melee-taken", I($"{hit.TotalDamage} from {Guid(hit.Attacker)} ({hit.VictimState})"), false);
                return null;
            }
            case WorldOpcode.SmsgAttackstart or WorldOpcode.SmsgAttackstop:
            {
                var r = new PacketReader(payload);
                ulong attacker = opcode == WorldOpcode.SmsgAttackstart ? r.ReadUInt64() : r.ReadPackedGuid();
                ulong victim = opcode == WorldOpcode.SmsgAttackstart ? r.ReadUInt64() : r.ReadPackedGuid();
                if (attacker != self && victim != self) return null;
                string what = opcode == WorldOpcode.SmsgAttackstart ? "attack-start" : "attack-stop";
                return new(what, I($"{Guid(attacker)} -> {Guid(victim)}"), false);
            }
            case WorldOpcode.SmsgAttackswingNotinrange: return new("attack-failed", "target not in range", true);
            case WorldOpcode.SmsgAttackswingBadfacing: return new("attack-failed", "facing the wrong way", true);
            case WorldOpcode.SmsgAttackswingNotstanding: return new("attack-failed", "not standing", true);
            case WorldOpcode.SmsgAttackswingDeadtarget: return new("attack-failed", "target is dead", true);
            case WorldOpcode.SmsgAttackswingCantAttack: return new("attack-failed", "cannot attack that target", true);
            case WorldOpcode.SmsgInventoryChangeFailure:
                return new("inventory-failed", I($"equip error {payload[0]}"), payload[0] != 0);
            case WorldOpcode.SmsgBuyFailed:
            {
                var r = new PacketReader(payload);
                ulong vendor = r.ReadUInt64();
                uint item = r.ReadUInt32();
                return new("buy-failed", I($"item {item} from {Guid(vendor)}: reason {r.ReadByte()}"), true);
            }
            case WorldOpcode.SmsgSellItem:
            {
                var r = new PacketReader(payload);
                ulong vendor = r.ReadUInt64();
                ulong item = r.ReadUInt64();
                byte error = r.ReadByte();
                return error == 0 ? new("sold", I($"item {Guid(item)}"), false) : new("sell-failed", I($"item {Guid(item)} to {Guid(vendor)}: reason {error}"), true);
            }
            case WorldOpcode.SmsgTrainerBuyFailed:
            {
                var r = new PacketReader(payload);
                r.ReadUInt64();
                uint spell = r.ReadUInt32();
                return new("train-failed", I($"spell {spell}: reason {r.ReadUInt32()}"), true);
            }
            case WorldOpcode.SmsgTrainerBuySucceeded:
            {
                var r = new PacketReader(payload);
                r.ReadUInt64();
                return new("trained", I($"spell {r.ReadUInt32()}"), false);
            }
            case WorldOpcode.SmsgQuestgiverQuestInvalid:
                return new("quest-invalid", I($"reason {new PacketReader(payload).ReadUInt32()}"), true);
            case WorldOpcode.SmsgQuestgiverQuestFailed:
            {
                var r = new PacketReader(payload);
                uint quest = r.ReadUInt32();
                return new("quest-failed", I($"quest {quest}: reason {r.ReadUInt32()}"), true);
            }
            case WorldOpcode.SmsgQuestlogFull: return new("quest-log-full", "the quest log is full", true);
            case WorldOpcode.SmsgQuestupdateComplete:
                return new("quest-objectives-done", I($"quest {new PacketReader(payload).ReadUInt32()}"), false);
            case WorldOpcode.SmsgQuestupdateAddKill:
            {
                QuestKillView kill = ScenarioDecoders.QuestKill(payload);
                return new("quest-progress", I($"quest {kill.Quest}: {kill.Entry} {kill.Count}/{kill.Required}"), false);
            }
            case WorldOpcode.SmsgQuestgiverQuestComplete:
            {
                QuestCompleteView done = ScenarioDecoders.QuestComplete(payload);
                return new("quest-rewarded", I($"quest {done.Quest}: {done.Experience} xp, {done.Money} copper"), false);
            }
            case WorldOpcode.SmsgQuestgiverQuestDetails or WorldOpcode.SmsgQuestgiverOfferReward or WorldOpcode.SmsgQuestgiverRequestItems:
            {
                var r = new PacketReader(payload);
                ulong giver = r.ReadUInt64();
                uint quest = r.ReadUInt32();
                string title = r.ReadCString();
                string what = opcode switch
                {
                    WorldOpcode.SmsgQuestgiverQuestDetails => "quest-details",
                    WorldOpcode.SmsgQuestgiverOfferReward => "quest-offer-reward",
                    _ => "quest-request-items",
                };
                return new(what, I($"quest {quest} '{title}' from {Guid(giver)}"), false);
            }
            case WorldOpcode.SmsgGossipMessage:
            {
                var r = new PacketReader(payload);
                ulong npc = r.ReadUInt64();
                r.ReadUInt32(); // text id
                uint count = r.ReadUInt32();
                var text = new StringBuilder(I($"{Guid(npc)}:"));
                for (uint i = 0; i < count && i < 32; i++)
                {
                    uint index = r.ReadUInt32();
                    r.ReadByte();
                    r.ReadByte();
                    text.Append(I($" [{index}] {r.ReadCString()}"));
                }

                uint quests = r.ReadUInt32();
                for (uint i = 0; i < quests && i < 32; i++)
                {
                    uint quest = r.ReadUInt32();
                    r.ReadUInt32();
                    r.ReadUInt32();
                    text.Append(I($" [quest {quest}] {r.ReadCString()}"));
                }

                return new("gossip", text.ToString(), false);
            }
            case WorldOpcode.SmsgQuestgiverQuestList:
            {
                var r = new PacketReader(payload);
                ulong npc = r.ReadUInt64();
                r.ReadCString();
                r.ReadUInt32();
                r.ReadUInt32();
                byte count = r.ReadByte();
                var text = new StringBuilder(I($"{Guid(npc)}:"));
                for (int i = 0; i < count; i++)
                {
                    uint quest = r.ReadUInt32();
                    r.ReadUInt32();
                    r.ReadUInt32();
                    text.Append(I($" [quest {quest}] {r.ReadCString()}"));
                }

                return new("quest-list", text.ToString(), false);
            }
            case WorldOpcode.SmsgGossipComplete: return new("gossip-closed", string.Empty, false);
            case WorldOpcode.SmsgListInventory:
            {
                var r = new PacketReader(payload);
                ulong vendor = r.ReadUInt64();
                byte count = r.ReadByte();
                var text = new StringBuilder(I($"{Guid(vendor)} sells {count}:"));
                for (int i = 0; i < count && i < 40; i++)
                {
                    r.ReadUInt32();
                    uint item = r.ReadUInt32();
                    r.ReadUInt32();
                    r.ReadUInt32();
                    uint price = r.ReadUInt32();
                    r.ReadUInt32();
                    r.ReadUInt32();
                    text.Append(I($" {item}@{price}"));
                }

                return new("vendor-list", text.ToString(), false);
            }
            case WorldOpcode.SmsgTrainerList:
            {
                var r = new PacketReader(payload);
                ulong trainer = r.ReadUInt64();
                r.ReadUInt32();
                return new("trainer-list", I($"{Guid(trainer)} teaches {r.ReadUInt32()} spells"), false);
            }
            case WorldOpcode.SmsgLootResponse:
            {
                LootResponseView loot = ScenarioDecoders.LootResponse(payload);
                string items = string.Join(' ', loot.Items.Select(i => I($"[{i.Slot}] {i.ItemId}x{i.Count}")));
                return new("loot", I($"{Guid(loot.Source)}: {loot.Gold} copper {items}").TrimEnd(), false);
            }
            case WorldOpcode.SmsgLootMoneyNotify: return new("loot-money", I($"{ScenarioDecoders.LootMoneyNotify(payload)} copper"), false);
            case WorldOpcode.SmsgItemPushResult:
            {
                var r = new PacketReader(payload);
                ulong owner = r.ReadUInt64();
                if (owner != self) return null;
                r.ReadUInt32();
                r.ReadUInt32();
                r.ReadUInt32();
                r.ReadByte();
                r.ReadUInt32();
                uint item = r.ReadUInt32();
                r.ReadUInt32();
                r.ReadUInt32();
                return new("item-received", I($"item {item} x{r.ReadUInt32()}"), false);
            }
            case WorldOpcode.SmsgLogXpgain:
            {
                XpGainView xp = ScenarioDecoders.XpGain(payload);
                return new("xp", I($"{xp.TotalXp} xp{(xp.Kill ? " (kill " + Guid(xp.Victim) + ")" : string.Empty)}"), false);
            }
            case WorldOpcode.SmsgLevelupInfo: return new("level-up", I($"level {new PacketReader(payload).ReadUInt32()}"), false);
            case WorldOpcode.SmsgNotification: return new("notification", new PacketReader(payload).ReadCString(), true);
            case WorldOpcode.SmsgAreaTriggerMessage:
            {
                var r = new PacketReader(payload);
                r.ReadUInt32();
                return new("area-message", r.ReadCString(), true);
            }
            case WorldOpcode.SmsgMessagechat:
            {
                if (payload.Length == 0) return null;
                var type = (ChatType)payload[0];
                if (type is not (ChatType.System or ChatType.Whisper or ChatType.Party or ChatType.Raid
                    or ChatType.Guild or ChatType.Officer or ChatType.Say or ChatType.Yell or ChatType.Emote)) return null;
                ChatMessageView chat = ScenarioDecoders.ChatMessage(payload);
                if (type is ChatType.Say or ChatType.Yell or ChatType.Emote && chat.Sender != self) return null;
                return new("chat-" + type.ToString().ToLowerInvariant(), I($"{Guid(chat.Sender)}: {chat.Message}"), false);
            }
            case WorldOpcode.SmsgPartyCommandResult:
            {
                PartyCommandResultView result = ScenarioDecoders.PartyCommandResult(payload);
                bool ok = (int)result.Result == 0;
                return new("party-result", I($"{result.Operation} {result.Name}: {result.Result}"), !ok);
            }
            case WorldOpcode.SmsgGroupInvite: return new("group-invite", "from " + ScenarioDecoders.GroupInvite(payload), false);
            case WorldOpcode.SmsgDuelRequested:
            {
                DuelRequestedView duel = ScenarioDecoders.DuelRequested(payload);
                return new("duel-requested", I($"by {Guid(duel.Initiator)} (arbiter {Guid(duel.Arbiter)})"), false);
            }
            case WorldOpcode.SmsgTradeStatus:
            {
                TradeStatusView trade = ScenarioDecoders.TradeStatus(payload);
                return new("trade", I($"{trade.Status}"), false);
            }
            case WorldOpcode.SmsgBattlefieldStatus: return new("battlefield-status", I($"{payload.Length} bytes"), false);
            default:
                return Generic(opcode, payload);
        }
    }

    private static string Guid(ulong guid) => "0x" + guid.ToString("X", CultureInfo.InvariantCulture);
}