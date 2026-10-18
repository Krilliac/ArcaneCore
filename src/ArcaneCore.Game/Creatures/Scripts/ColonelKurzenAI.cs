using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// Colonel Kurzen (813), Stranglethorn Vale: mangos-classic ScriptDev2 <c>mob_colonel_kurzen</c> (eastern_kingdoms/stranglethorn_vale.cpp:110-160).
/// In a fight he throws a Smoke Bomb (8817) after 6-12 s, which leaves him in Stealth (8822); 2-6 s later he Garrotes (8818) his victim if he
/// is still stealthed, and the smoke bomb comes again 6-12 s after that. The source also sets combat-only stealth before the bomb; ArcaneCore
/// has no such flag, so the stealth is the spell's own aura.
/// </summary>
public sealed class ColonelKurzenAI(Creature creature) : ScriptedAI(creature)
{
    public const uint Entry = 813, SpellSmokeBomb = 8817, SpellGarrote = 8818, SpellStealth = 8822;

    private uint _vanishMs, _garroteMs;

    public bool GarrotePending => _garroteMs != 0;

    protected override void Reset()
    {
        _vanishMs = RandomMs(6000, 12_000);
        _garroteMs = 0;
    }

    private uint RandomMs(int min, int max) => (uint)(System?.RandomInt(min, max) ?? min);

    public override void OnUpdate(uint diffMs)
    {
        if (!UpdateVictim() || Victim is not { } victim)
        {
            return;
        }

        if (_garroteMs != 0)
        {
            if (_garroteMs <= diffMs)
            {
                if (System?.HasAura(Me, SpellStealth) == true)
                {
                    DoCast(victim, SpellGarrote);
                }

                _garroteMs = 0;
                _vanishMs = RandomMs(6000, 12_000);
            }
            else
            {
                _garroteMs -= diffMs;
            }

            return;
        }

        if (_vanishMs <= diffMs)
        {
            DoCast(Me, SpellSmokeBomb);
            _garroteMs = RandomMs(2000, 6000);
        }
        else
        {
            _vanishMs -= diffMs;
        }
    }
}
