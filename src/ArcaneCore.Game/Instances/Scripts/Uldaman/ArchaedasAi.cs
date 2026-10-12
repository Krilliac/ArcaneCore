using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Instances.Scripts.Uldaman;

/// <summary>ScriptDev2 boss_archaedasAI (mangos-classic uldaman/boss_archaedas.cpp:
/// Reset, Aggro, JustDied, JustReachedHome, UpdateAI and AwakenEarthenArchaedas).</summary>
public sealed class ArchaedasAi(Creature creature, UldamanInstance instance) : ScriptedAI(creature)
{
    public const uint AwakenVisual = 10347, GroundTremor = 6524, AwakenGuardians = 10252;
    public const uint AwakenWarders = 10258, AwakenDwarf = 10259;

    /// <summary>
    /// ClassicDB z2815 spell_template 10252 / 10258: TARGET_ENUM_UNITS_SCRIPT_AOE_AT_SRC_LOC around the caster with EffectRadiusIndex 12
    /// (SpellRadius.dbc: 100 yd), the targets limited by spell_script_target to 7076 / 10120; 10258 also has MaxAffectedTargets 2, which
    /// cmangos Spell::FilterTargetMap (SCHEME_RANDOM) meets by dropping random units.
    /// </summary>
    public const float AwakenRadius = 100f;
    public const int WardersAwakened = 2;
    private uint _awakeningMs = 1000, _dwarfMs = 10_000, _tremorMs = 7000;
    private int _subevent, _healthPhase = 1;
    private bool _dwarvesAwaken;

    protected override void Reset()
    {
        _awakeningMs = 1000;
        _dwarfMs = 10_000;
        _tremorMs = (uint)Random.Shared.Next(7000, 14001);
        _subevent = 0;
        _healthPhase = 1;
        _dwarvesAwaken = false;
        DoCast(Me, UldamanInstance.SpellFreezeAnim);
        Me.UnitFlags |= UnitFlags.NotSelectable;
    }

    public override void OnAggro(Unit target) => instance.SetData(UldamanInstance.TypeArchaedas, EncounterState.InProgress);
    public override void OnDeath(Unit? killer) => instance.SetData(UldamanInstance.TypeArchaedas, EncounterState.Done);
    public override void OnReachedHome() => instance.SetData(UldamanInstance.TypeArchaedas, EncounterState.Fail);
    public override void OnKilledUnit(Unit victim) => System?.SayText(Me, -1070004);

    public override void OnUpdate(uint diffMs)
    {
        if (instance.GetData(UldamanInstance.TypeArchaedas) == EncounterState.Special && _subevent < 3)
        {
            if (_awakeningMs > diffMs) _awakeningMs -= diffMs;
            else
            {
                switch (_subevent++)
                {
                    case 0:
                        DoCast(Me, AwakenVisual);
                        _awakeningMs = 2000;
                        break;
                    case 1:
                        System?.SayText(Me, -1070005);
                        _awakeningMs = 3000;
                        break;
                    case 2:
                        System?.SayText(Me, -1070001);
                        System?.RemoveAuras(Me, UldamanInstance.SpellFreezeAnim);
                        Me.UnitFlags &= ~UnitFlags.NotSelectable;
                        if (instance.GetData64(UldamanInstance.DataEventStarter) is { } raw
                            && Me.Map?.FindPlayer(new ObjectGuid(raw)) is { IsAlive: true } player)
                            AttackStart(player);
                        else EnterEvadeMode();
                        break;
                }
            }
        }

        if (!UpdateVictim()) return;
        float healthPercent = Me.MaxHealth == 0 ? 100f : 100f * Me.Health / Me.MaxHealth;
        if (_healthPhase <= 2 && healthPercent < 100f - 33.4f * _healthPhase)
        {
            uint spell = _healthPhase == 1 ? AwakenGuardians : AwakenWarders;
            if (DoCast(Me, spell) == CreatureCastResult.Ok)
            {
                System?.SayText(Me, _healthPhase == 1 ? -1070002 : -1070003);
                _healthPhase++;
            }
        }

        if (!_dwarvesAwaken && healthPercent >= 33f)
        {
            if (_dwarfMs > diffMs) _dwarfMs -= diffMs;
            else if (instance.ClosestDwarfNotInCombat(Me) is { } dwarf)
            {
                if (DoCast(dwarf, AwakenDwarf) == CreatureCastResult.Ok)
                {
                    Awaken(dwarf);
                    _dwarfMs = (uint)Random.Shared.Next(9000, 12001);
                }
            }
            else _dwarvesAwaken = true;
        }

        if (_tremorMs > diffMs) _tremorMs -= diffMs;
        else if (DoCast(Me, GroundTremor) == CreatureCastResult.Ok)
            _tremorMs = (uint)Random.Shared.Next(8000, 17001);
    }

    // The cast the substitutes below belong to, and the room warders it already woke in place of a hallway pick (world thread only).
    private object? _warderCast;
    private readonly List<Creature> _substitutes = [];

    /// <summary>
    /// Wakes <paramref name="dwarf"/>: removes Stoned and orders an attack on Archaedas' victim. Spec 70-mob_archaeras_add 11.4 (recommended): a
    /// hallway Vault Warder is never woken; its pick wakes a sleeping room warder instead, so the cast of Awaken Vault Warder (two random
    /// targets in 100 yd, picked among all four warders) still wakes the two room warders. <paramref name="cast"/> identifies the spell cast the
    /// pick belongs to: a room warder that an earlier hallway pick of the SAME cast already woke as its substitute does not count its own pick
    /// as done, it wakes another sleeping room warder, so neither warder is woken twice while the other sleeps. Null: no grouping.
    /// </summary>
    internal void Awaken(Creature dwarf, object? cast = null)
    {
        if (dwarf.Template.Entry == UldamanInstance.VaultWarder)
        {
            if (!ReferenceEquals(cast, _warderCast))
            {
                _warderCast = cast;
                _substitutes.Clear();
            }

            if (UldamanInstance.IsHallwayWarder(dwarf) || (cast is not null && _substitutes.Remove(dwarf)))
            {
                if (instance.NextDormantRoomWarder() is not { } substitute) return;
                if (cast is not null) _substitutes.Add(substitute);
                dwarf = substitute;
            }
        }

        System?.RemoveAuras(dwarf, UldamanInstance.SpellStoned);
        if (Victim is { } victim) dwarf.AI?.AttackStart(victim);
    }
}
