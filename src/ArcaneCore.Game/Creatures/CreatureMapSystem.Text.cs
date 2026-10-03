using System.Numerics;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Game.Creatures;

/// <summary>Creature speech (cmangos DoScriptText).</summary>
public sealed partial class CreatureMapSystem
{
    /// <summary>
    /// Speak a creature_ai_texts entry (cmangos DoScriptText): type 0 say (25 yd), 1 yell
    /// (300 yd), 2 text emote (25 yd), 3 boss emote and 6 zone yell (whole map), 4 and 5 whisper
    /// to the target player. <c>$N</c> becomes the target's name; a non-zero emote plays too.
    /// </summary>
    public void Say(Creature creature, CreatureAiText text, Unit? target)
    {
        ArgumentNullException.ThrowIfNull(creature);
        ArgumentNullException.ThrowIfNull(text);
        string content = text.Content;
        if (target is Player named)
        {
            content = content.Replace("$N", named.Name, StringComparison.Ordinal).Replace("$n", named.Name, StringComparison.Ordinal);
        }

        ObjectGuid targetGuid = target?.Guid ?? default;
        string name = creature.Template.Name;
        switch (text.Type)
        {
            case 1:
                Broadcast(ChatType.MonsterYell, CreatureChatPackets.YellRange);
                break;
            case 2:
                Broadcast(ChatType.MonsterEmote, CreatureChatPackets.TextEmoteRange);
                break;
            case 3:
                Broadcast(ChatType.MonsterEmote, 0);
                break;
            case 4:
            case 5:
                if (target is Player player && ReferenceEquals(player.Map, Map))
                {
                    player.Session.Send(WorldOpcode.SmsgMessagechat,
                        CreatureChatPackets.BuildMonsterMessage(ChatType.MonsterWhisper, text.Language, creature.Guid, name, targetGuid, content));
                }

                break;
            case 6:
                Broadcast(ChatType.MonsterYell, 0);
                break;
            default:
                Broadcast(ChatType.MonsterSay, CreatureChatPackets.SayRange);
                break;
        }

        if (text.Emote != 0)
        {
            Map.BroadcastToObservers(creature, WorldOpcode.SmsgEmote, CreatureChatPackets.BuildEmote(text.Emote, creature.Guid));
        }

        void Broadcast(ChatType type, float range)
            => Map.BroadcastInRange(creature, range, WorldOpcode.SmsgMessagechat,
                CreatureChatPackets.BuildMonsterMessage(type, text.Language, creature.Guid, name, targetGuid, content), includeSelf: false);
    }
}
