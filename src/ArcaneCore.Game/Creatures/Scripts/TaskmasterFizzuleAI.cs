using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// Taskmaster Fizzule (entry 7233, The Barrens): mangos-classic ScriptDev2 <c>npc_taskmaster_fizzule</c> (kalimdor/the_barrens.cpp:144-272,
/// registered :1105-1109). He is a hostile Venture Co. foreman until a player calms him with the quest flares: the first Flare (10113) makes him
/// immune to players for two minutes, the second turns him friendly (faction 35 until he is home), and a Salute at that point makes him salute
/// back and offer his quests. Goblin Folly (10137) does all of it at once. When the two minutes are up he loses the quest flag and the immunity
/// and evades home, which gives his faction back (HandleFlareOrKneel, :230-253). While calmed his evade only stops the fight where he stands
/// (EnterEvadeMode, :172-181).
/// </summary>
public sealed class TaskmasterFizzuleAI(Creature creature) : ScriptedAI(creature)
{
    public const uint Entry = 7233, FactionFriendly = 35, SpellFlare = 10113, SpellFolly = 10137;
    public const uint TextEmoteSalute = 78, EmoteOneshotSalute = 66, CalmMs = 120_000;

    private uint _resetMs;
    private byte _flareCount;

    /// <summary>The flares and the salute he has taken since he last calmed down (the source's flareCount).</summary>
    public byte FlareCount => _flareCount;

    public bool IsCalmed => _resetMs > 0;

    protected override void Reset()
    {
    }

    /// <summary>EnterEvadeMode (:172-181): calmed, he only drops his evade-removable auras and the fight; otherwise the normal evade.</summary>
    public override bool OnEnterEvadeMode()
    {
        if (_resetMs == 0)
        {
            return false;
        }

        if (System is { } system)
        {
            system.ResetAurasOnEvade(Me);
            system.StopCombatInPlace(Me);
        }

        return true;
    }

    /// <summary>SpellHit (:183-206): only a player's Flare (up to the second) or Goblin Folly moves him along.</summary>
    public override void OnSpellHit(Unit caster, SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(caster);
        ArgumentNullException.ThrowIfNull(spell);
        if (caster is not Player)
        {
            return;
        }

        switch (spell.Id)
        {
            case SpellFlare:
                if (_flareCount < 2)
                {
                    _flareCount++;
                    HandleFlareOrKneel();
                }

                break;
            case SpellFolly:
                _flareCount = 4;
                HandleFlareOrKneel();
                break;
        }
    }

    /// <summary>ReceiveEmote (:255-265): a salute while he is friendly (count 2) makes him salute back.</summary>
    public override void OnReceiveEmote(Player player, uint textEmote)
    {
        if (textEmote == TextEmoteSalute && _flareCount == 2)
        {
            _flareCount++;
            HandleFlareOrKneel();
        }
    }

    public override void OnUpdate(uint diffMs)
    {
        if (_resetMs > 0)
        {
            if (_resetMs <= diffMs)
            {
                _resetMs = 0;
                _flareCount = 0;
                Me.NpcFlags &= ~(uint)NpcFlags.QuestGiver;
                Me.UnitFlags &= ~UnitFlags.ImmuneToPlayer;
                EnterEvadeMode();
            }
            else
            {
                _resetMs -= diffMs;
            }
        }

        UpdateVictim();
    }

    /// <summary>HandleFlareOrKneel (:230-253).</summary>
    private void HandleFlareOrKneel()
    {
        switch (_flareCount)
        {
            case 1:
                _resetMs = CalmMs;
                Me.UnitFlags |= UnitFlags.ImmuneToPlayer;
                break;
            case 2:
                Me.SetFactionTemporary(FactionFriendly, TemporaryFactionFlags.RestoreReachHome);
                break;
            case 3:
                Me.NpcFlags |= (uint)NpcFlags.QuestGiver;
                System?.PlayEmote(Me, EmoteOneshotSalute);
                break;
            case 4:
                _resetMs = CalmMs;
                Me.UnitFlags |= UnitFlags.ImmuneToPlayer;
                Me.SetFactionTemporary(FactionFriendly, TemporaryFactionFlags.RestoreReachHome);
                Me.NpcFlags |= (uint)NpcFlags.QuestGiver;
                System?.PlayEmote(Me, EmoteOneshotSalute);
                break;
        }
    }
}
