using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// Henze Faulk (6172) and Narm Faulk (6177), the bodies of the paladin Redemption quests (1779 and 1781, leading to 1783 and 1786): mangos-classic
/// ScriptDev2 <c>npc_redemption_target</c> (world/npcs_special.cpp:847-994) with its <c>PaladinQuestReviveSelf</c> spell script on Symbol of
/// Life (8593). He lies dead-looking (dead dynamic flag, dead stand state) and offers no quests. The Symbol of Life revives him: he casts Quest
/// Self Healing (25155) and stands. Three seconds later he faces the paladin, at four seconds he offers quests again and says broadcast text
/// 2283, and at seven seconds he kneels. Two minutes after the revive he evades, which lays him back down.
/// </summary>
public sealed class RedemptionTargetAI(Creature creature) : CreatureAI(creature)
{
    public const uint NpcHenzeFaulk = 6172, NpcNarmFaulk = 6177, SpellSymbolOfLife = 8593, SpellQuestSelfHealing = 25155;
    public const int SayHealHenzeNarmFaulk = 2283;
    private const uint DynFlagDead = 0x20, EmoteOneshotKneel = 16, EvadeAfterMs = 120_000;

    private uint _evadeMs, _orientationMs, _textMs, _emoteMs;
    private ObjectGuid _playerGuid;

    public bool Revived => _evadeMs != 0;

    public override void OnRespawn() => LieDown();

    public override void OnEvade()
    {
        base.OnEvade();
        LieDown();
    }

    private void LieDown()
    {
        _evadeMs = _orientationMs = _textMs = _emoteMs = 0;
        Me.NpcFlags &= ~(uint)NpcFlags.QuestGiver;
        Me.SetUInt32(UpdateFields.UnitDynamicFlags, Me.GetUInt32(UpdateFields.UnitDynamicFlags) | DynFlagDead);
        Me.StandState = StandState.Dead;
    }

    public override void OnSpellHit(Unit caster, SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(caster);
        ArgumentNullException.ThrowIfNull(spell);
        base.OnSpellHit(caster, spell);
        if (spell.Id == SpellSymbolOfLife)
        {
            ReviveSelf(caster.Guid);
        }
    }

    /// <summary>DoReviveSelf: nothing until he has reset again.</summary>
    public void ReviveSelf(ObjectGuid by)
    {
        if (_evadeMs != 0)
        {
            return;
        }

        System?.CastSpell(Me, SpellQuestSelfHealing, Me, triggered: false);
        Me.SetUInt32(UpdateFields.UnitDynamicFlags, Me.GetUInt32(UpdateFields.UnitDynamicFlags) & ~DynFlagDead);
        Me.StandState = StandState.Stand;
        _evadeMs = EvadeAfterMs;
        _playerGuid = by;
        _orientationMs = 3000;
        _textMs = 4000;
        _emoteMs = 7000;
    }

    private static bool Tick(ref uint timer, uint diffMs)
    {
        if (timer == 0)
        {
            return false;
        }

        if (timer <= diffMs)
        {
            timer = 0;
            return true;
        }

        timer -= diffMs;
        return false;
    }

    public override void OnUpdate(uint diffMs)
    {
        if (System is not { } system)
        {
            return;
        }

        if (Tick(ref _orientationMs, diffMs))
        {
            if (system.Map.FindPlayer(_playerGuid) is { } player)
            {
                system.SetFacingTo(Me, MathF.Atan2(player.Y - Me.Y, player.X - Me.X));
            }
            else
            {
                _orientationMs = 1; // cmangos waits for the player: try again next tick
            }
        }

        if (Tick(ref _textMs, diffMs))
        {
            if (system.Map.FindPlayer(_playerGuid) is { } player)
            {
                // Quests 1783 and 1786 need his quest giver flag.
                Me.NpcFlags |= (uint)NpcFlags.QuestGiver;
                system.SayText(Me, SayHealHenzeNarmFaulk, player);
            }
            else
            {
                _textMs = 1;
            }
        }

        if (Tick(ref _emoteMs, diffMs))
        {
            system.PlayEmote(Me, EmoteOneshotKneel);
        }

        if (Tick(ref _evadeMs, diffMs))
        {
            EnterEvadeMode();
            LieDown();
        }
    }
}
