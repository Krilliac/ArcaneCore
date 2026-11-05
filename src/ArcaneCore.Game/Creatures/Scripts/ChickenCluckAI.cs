using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// Chicken (entry 620), "Cluck!" (quest 3861): mangos-classic ScriptDev2 <c>npc_chicken_cluck</c> (world/npcs_special.cpp at
/// D:/refs/mangos-classic, npc_chicken_cluckAI). The chicken is a neutral critter (faction 31) without the quest-giver flag. A /chicken at
/// it has a 1 in 50 chance to make it friendly (faction 35), quest-giver and say its first line; a /cheer from a player whose Cluck! is
/// complete does the same with the second line. 20 seconds after that it evades, which resets faction and flag.
/// </summary>
public sealed class ChickenCluckAI(Creature creature) : CreatureAI(creature)
{
    public const uint Entry = 620, QuestCluck = 3861, FactionFriendly = 35, FactionChicken = 31, ResetFlagMs = 20_000;
    public const uint TextEmoteCheer = 21, TextEmoteChicken = 22;
    public const int SayClucks1 = -1000204, SayClucks2 = -1000205;

    private uint _resetMs = ResetFlagMs;
    private bool _started;

    /// <summary>True while the chicken offers the quest (the quest-giver flag is up).</summary>
    public bool Offering => (Me.NpcFlags & (uint)NpcFlags.QuestGiver) != 0;

    private void Reset()
    {
        _started = true;
        _resetMs = ResetFlagMs;
        Me.FactionTemplate = FactionChicken;
        Me.NpcFlags &= ~(uint)NpcFlags.QuestGiver;
    }

    public override void OnEvade() => Reset();

    public override void OnRespawn() => Reset();

    public override void OnReceiveEmote(Player player, uint textEmote)
    {
        if (!_started)
        {
            Reset();
        }

        if (textEmote == TextEmoteChicken && System?.RandomInt(0, 49) == 0)
        {
            Offer(SayClucks1);
        }
        else if (textEmote == TextEmoteCheer && System?.QuestComplete(player, QuestCluck) == true)
        {
            Offer(SayClucks2);
        }
    }

    private void Offer(int text)
    {
        Me.NpcFlags |= (uint)NpcFlags.QuestGiver;
        Me.FactionTemplate = FactionFriendly;
        System?.SayText(Me, text);
    }

    public override void OnUpdate(uint diffMs)
    {
        if (!_started)
        {
            Reset();
        }

        // The flag is reset after a while so the next player has to start the event again.
        if (Offering)
        {
            if (_resetMs < diffMs)
            {
                EnterEvadeMode();
                Reset(); // the engine's evade of an idle creature may not reach OnEvade (as PluckyJohnsonAI)
            }
            else
            {
                _resetMs -= diffMs;
            }
        }

        UpdateVictim();
    }
}
