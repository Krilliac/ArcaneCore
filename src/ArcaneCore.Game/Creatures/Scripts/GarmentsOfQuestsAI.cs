using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// The wounded guards of the priest "Garments of ..." quests: mangos-classic ScriptDev2 <c>npc_garments_of_quests</c> (world/npcs_special.cpp:535-745).
/// The pairs are Sentinel Shaya 12429 with Garments of the Moon (5621), Guard Roberts 12423 with Garments of the Light (5624), Mountaineer
/// Dolf 12427 with Garments of the Light (5625), Grunt Kor'ja 12430 with Garments of Spirit (5648) and Deathguard Kel 12428 with Garments of
/// Darkness (5650). The guard kneels, flagged for PvP, at 70% health. A priest on the quest heals him with Lesser Heal rank 2 (2052), so he
/// stands and thanks them. Power Word: Fortitude rank 1 (1243) then gives the speak-to credit, and five seconds later he says goodbye and
/// runs off along his escort path (creature_movement_template).
/// </summary>
public sealed class GarmentsOfQuestsAI(Creature creature) : EscortAI(creature)
{
    public const uint SpellLesserHealR2 = 2052, SpellFortitudeR1 = 1243;
    public const uint EntryShaya = 12429, EntryRoberts = 12423, EntryDolf = 12427, EntryKorja = 12430, EntryDgKel = 12428;
    public const int SayCommonHealed = -1000231;
    public static readonly uint[] Entries = [EntryShaya, EntryRoberts, EntryDolf, EntryKorja, EntryDgKel];

    private ObjectGuid _playerGuid;
    private bool _healed, _canRun;
    private uint _runAwayMs = 5000;

    public bool IsHealed => _healed;
    public bool CanRun => _canRun;

    /// <summary>The quest, thanks line and goodbye line of each guard.</summary>
    public static (uint Quest, int Thanks, int Goodbye) For(uint entry) => entry switch
    {
        EntryShaya => (5621, -1000262, -1000263),
        EntryRoberts => (5624, -1000256, -1000257),
        EntryDolf => (5625, -1000260, -1000261),
        EntryKorja => (5648, -1000258, -1000259),
        EntryDgKel => (5650, -1000232, -1000233),
        _ => (0, 0, 0),
    };

    protected override void JustSpawned() => ResetGuard();

    protected override void Reset()
    {
        if (!HasEscortState(EscortState.Escorting))
        {
            ResetGuard();
        }
    }

    private void ResetGuard()
    {
        _playerGuid = ObjectGuid.Empty;
        _healed = false;
        _canRun = false;
        _runAwayMs = 5000;
        Me.StandState = StandState.Kneel;
        Me.UnitFlags |= UnitFlags.Pvp; // classic only: the special PvP flag
        Me.Health = Math.Max(1u, (uint)((ulong)Me.MaxHealth * 70 / 100));
    }

    public override void OnSpellHit(Unit caster, SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(caster);
        ArgumentNullException.ThrowIfNull(spell);
        base.OnSpellHit(caster, spell);
        if (spell.Id is not (SpellLesserHealR2 or SpellFortitudeR1) || Me.Combat.IsInCombat || (_healed && _canRun)
            || caster is not Player player || System is not { } system)
        {
            return;
        }

        (uint quest, int thanks, _) = For(Me.Entry);
        if (quest == 0 || !system.QuestIncomplete(player, quest))
        {
            return;
        }

        if (_healed && !_canRun && spell.Id == SpellFortitudeR1)
        {
            system.SayText(Me, thanks, player);
            _canRun = true;
        }
        else if (!_healed && spell.Id == SpellLesserHealR2)
        {
            _playerGuid = player.Guid;
            Me.StandState = StandState.Stand;
            system.SayText(Me, SayCommonHealed, player);
            _healed = true;
        }

        if (_canRun)
        {
            system.TalkedToCreature(player, Me.Entry, Me.Guid);
        }
    }

    protected override void WaypointReached(uint pointId)
    {
    }

    protected override void UpdateEscortAI(uint diffMs)
    {
        if (_canRun && !Me.Combat.IsInCombat && !HasEscortState(EscortState.Escorting))
        {
            if (_runAwayMs <= diffMs)
            {
                if (System?.Map.FindPlayer(_playerGuid) is { } player)
                {
                    System.SayText(Me, For(Me.Entry).Goodbye, player);
                    Start(run: true);
                }
                else
                {
                    EnterEvadeMode(); // something went wrong
                }

                _runAwayMs = 30_000;
            }
            else
            {
                _runAwayMs -= diffMs;
            }
        }

        UpdateVictim();
    }
}
