using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Instances.Scripts.Classic;

/// <summary>
/// The first elemental spell changes the ectoplasm's colour and grants that school's immunity
/// for ten seconds. vmangos wailing_caverns/wailing_caverns.cpp,
/// EvolvingEctoplasmAI::SpellHit, UpdateAI and Reset (lines 633-706).
/// </summary>
public sealed class EvolvingEctoplasmAi(Creature creature) : ScriptedAI(creature)
{
    private static readonly uint[] ElementalAuras = [7943, 7944, 7945, 7946, 7743, 7940, 7942, 7941];
    private uint _immuneTimer;
    private bool _immune;

    public override void OnSpellHit(Unit caster, SpellInfo spell)
    {
        if (_immune)
        {
            return;
        }

        (uint transform, uint immune) = spell.School switch
        {
            SpellSchool.Fire => (7943u, 7942u),
            SpellSchool.Frost => (7944u, 7940u),
            SpellSchool.Nature => (7945u, 7941u),
            SpellSchool.Shadow => (7946u, 7743u),
            _ => (0u, 0u),
        };
        if (transform == 0)
        {
            return;
        }

        DoCast(Me, transform, triggered: true);
        DoCast(Me, immune, triggered: true);
        _immuneTimer = 10_000;
        _immune = true;
    }

    public override void OnUpdate(uint diffMs)
    {
        // vmangos uses '<', so the auras expire on the first update after ten full seconds.
        if (_immuneTimer < diffMs)
        {
            ClearElementalAuras();
            _immune = false;
        }
        else
        {
            _immuneTimer -= diffMs;
        }

        base.OnUpdate(diffMs);
    }

    protected override void Reset()
    {
        ClearElementalAuras();
        _immuneTimer = 0;
        _immune = false;
    }

    private void ClearElementalAuras()
    {
        foreach (uint spell in ElementalAuras)
        {
            System?.RemoveAuras(Me, spell);
        }
    }
}
