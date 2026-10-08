using ArcaneCore.Game.Spells.Procs;

namespace ArcaneCore.Game.Spells.Paladin;

/// <summary>
/// Registers the paladin scripts that are not <see cref="Scripts.ISpellScript"/>s (discovered <see cref="ISpellHandlerModule"/>, so every spell
/// system has them): the seal, blessing, aura and judgement rules and AURA_STATE_JUDGEMENT (<see cref="PaladinAuraRules"/>), the Seal of
/// Righteousness and Judgement of Light / Wisdom procs, the Judgement of Command and Hammer of Wrath damage, Forbearance after a bubble with its cast check, and
/// the Consecration tick (<see cref="ConsecrationScript"/>).
/// </summary>
public sealed class PaladinScriptsModule : ISpellHandlerModule
{
    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        PaladinAuraRules.Install(system);

        var consecration = new ConsecrationScript();
        foreach (uint rank in ConsecrationScript.Ranks)
        {
            system.RegisterPeriodicDamageScript(rank, consecration);
        }

        IProcScript seal = new SealOfRighteousnessProc();
        foreach (uint rank in SealOfRighteousnessProc.DamageSpells.Keys)
        {
            system.RegisterProcScript(rank, seal);
        }

        IProcScript judgement = new JudgementOfLightWisdomProc();
        foreach (uint rank in JudgementOfLightWisdomProc.TriggerSpells.Keys)
        {
            system.RegisterProcScript(rank, judgement);
        }

        system.RegisterValueModifier(new JudgementOfCommandDamage(system));
        system.RegisterValueModifier(new HammerOfWrathDamage(system));
        system.RegisterObserver(new ForbearanceObserver(system));
        system.RegisterCastCheck(new PositiveSpellImmunityCheck());
    }
}
