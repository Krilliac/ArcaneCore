namespace ArcaneCore.Game.Spells.Procs.Talents;

/// <summary>
/// Registers the talent proc scripts of vmangos <c>Unit::HandleDummyAuraProc</c> (discovered <see cref="ISpellHandlerModule"/>, so every spell system
/// has them): Eye for an Eye, Sweeping Strikes, Retaliation, Vampiric Embrace and Blade Flurry by spell id, Magic Absorption and Master of
/// Elements by their mage family icon (every rank shares it, and the spell table is often loaded after the spell system is built). The talent
/// coverage report (<c>TalentEffectCoverage</c>) counts a talent as handled through these same registrations.
/// </summary>
public sealed class TalentProcScriptsModule : ISpellHandlerModule
{
    /// <summary>SPELLFAMILY_MAGE.</summary>
    public const uint FamilyMage = 3;

    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        var eyeForAnEye = new EyeForAnEyeProc();
        foreach (uint rank in EyeForAnEyeProc.Ranks)
        {
            system.RegisterProcScript(rank, eyeForAnEye);
        }

        var sweepingStrikes = new SweepingStrikesProc();
        foreach (uint rank in SweepingStrikesProc.Ranks)
        {
            system.RegisterProcScript(rank, sweepingStrikes);
        }

        system.RegisterProcScript(RetaliationProc.Spell, new RetaliationProc());
        system.RegisterProcScript(VampiricEmbraceProc.Spell, new VampiricEmbraceProc());
        system.RegisterProcScript(BladeFlurryProc.Spell, new BladeFlurryProc());
        system.RegisterIconProcScript(FamilyMage, MagicAbsorptionProc.Icon, new MagicAbsorptionProc());
        system.RegisterIconProcScript(FamilyMage, MasterOfElementsProc.Icon, new MasterOfElementsProc());
    }
}
