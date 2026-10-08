using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Instances.Scripts.Uldaman;

/// <summary>ScriptDev2 boss_archaedasAI (mangos-classic uldaman/boss_archaedas.cpp:
/// Reset, Aggro, JustDied, JustReachedHome, UpdateAI and AwakenEarthenArchaedas).</summary>
public sealed class ArchaedasAi(Creature creature, UldamanInstance instance) : CreatureAI(creature)
{
    public const uint AwakenVisual = 10347, GroundTremor = 6524, AwakenGuardians = 10252;
    public const uint AwakenWarders = 10258, AwakenDwarf = 10259;
    private uint _awakeningMs = 1000, _dwarfMs = 10_000, _tremorMs = 7000;
    private int _subevent, _healthPhase = 1;
    private bool _dwarvesAwaken;

    public override void OnRespawn()
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
                AwakenGroup(_healthPhase == 1 ? UldamanInstance.Guardian : UldamanInstance.VaultWarder);
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

    private void AwakenGroup(uint entry)
    {
        if (System is not { } system) return;
        foreach (Creature dwarf in system.Creatures.Where(c => c.Template.Entry == entry && c.IsAlive)) Awaken(dwarf);
    }

    private void Awaken(Creature dwarf)
    {
        System?.RemoveAuras(dwarf, UldamanInstance.SpellStoned);
        if (Victim is { } victim) dwarf.AI?.AttackStart(victim);
    }
}
