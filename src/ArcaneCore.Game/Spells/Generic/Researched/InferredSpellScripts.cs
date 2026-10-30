using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Game.Spells.Scripts;

namespace ArcaneCore.Game.Spells.Generic.Researched;

// Three spells that no reference core (vmangos, cmangos, mangoszero) and no public source documents. The maintainer decided on 2026-10-29 to implement the
// best-evidence behaviour for them. Every behaviour below is inferred, not verified against a retail capture; the evidence is Spell.dbc 5875 (effect
// types, targets, radius, periodic trigger chains), the spell names and tooltips, and the classic-db / vmangos rows named on each class. They are listed
// as inferred in docs/areas/spells.md. The fourth spell of the same decision, Enigma Blizzard Bonus 26127, is a channel rule and lives in
// Spells/Mage/EnigmaBlizzardBonus.cs.

/// <summary>
/// ToWoW - Sillithus PvP Flag 30176. Behaviour inferred, not verified against a retail capture.
/// <para>
/// Evidence: Spell.dbc gives 30176 one DUMMY effect on the caster. It is the periodic trigger (EffectTriggerSpell 30176, amplitude 15000 ms, target
/// self) of the Silithyst carrier aura 29519 (the third effect of 29519, which the clicker takes from Silithus Flag Click 29518, vmangos
/// SpellEffects.cpp:1404-1411). The name says it is the Silithus PvP flag. Every 15 s the carrier is therefore flagged for PvP when it is not, so a carrier
/// who is not flagged is flagged within 15 s. A flagged player and any non-player carrier are left alone, so the flag's own 5-minute timer is not refreshed here.
/// </para>
/// The flag goes through <see cref="MapCombat.UpdatePvp"/>, the call the hostile-area rule (<c>PvpAreaTracker.OnZoneChanged</c>) makes.
/// </summary>
[SpellScript(30176)]
public sealed class SilithusPvpFlagScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Target is Player player && (player.UnitFlags & UnitFlags.Pvp) == 0)
        {
            MapCombat.UpdatePvp(player, true);
        }
    }
}

/// <summary>
/// Silithyst Cap Reward 31247. Behaviour inferred, not verified against a retail capture.
/// <para>
/// Evidence: Spell.dbc gives 31247 two effects on the caster, a SCRIPT_EFFECT and effect 122 with misc 609 (Cenarion Circle, Faction.dbc) and value 10
/// (base points 9 plus a die of 1). vmangos OutdoorPvP/OutdoorPvPSI.cpp:250 casts it on the player at the turn-in with the comment "Cenarian faction"
/// (ArcaneCore OutdoorPvP/Silithus.cs casts it from <c>SilithusCatalog.SpellSilithystCapReward</c>), but vmangos treats effect 122 as EffectUnused
/// (SpellEffects.cpp:182) and has no script for the SCRIPT_EFFECT, so no core says what it does. The turn-in therefore gives the player +10 reputation
/// with faction 609.
/// </para>
/// <para>
/// This is a handler of spell 31247 only: effect 122 keeps no meaning in general (it stays an unhandled effect). The script reads the faction and the
/// value from the spell's own effect 122 and gives the reputation through the same call as SPELL_EFFECT_REPUTATION (<c>ReputationSpellHandlers
/// .GrantSpellReputation</c>: <c>ReputationService.Gain</c> with <c>ReputationSource.Spell</c>, then <c>ModifyReputation</c>), so the Diplomacy aura,
/// the spell rate, spillover and the standing caps apply as they do for any spell reputation.
/// </para>
/// </summary>
[SpellScript(31247)]
public sealed class SilithystCapRewardScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Effect.Effect != SpellEffectName.ScriptEffect || context.Target is not Player player
            || ReputationEnvironment.For(context.System) is not { } service)
        {
            return;
        }

        for (int i = 0; i < context.Spell.Effects.Count; i++)
        {
            if (context.Spell.Effects[i].Effect != SpellEffectName.Unk122)
            {
                continue;
            }

            int value = context.Spell.CalculateEffectValue(i, context.System.CasterLevelOf(context.Caster), context.System.Random);
            ReputationSpellHandlers.GrantSpellReputation(service, player, (uint)context.Spell.Effects[i].MiscValue, value);
            return;
        }
    }
}

/// <summary>
/// Agro Drones Effect 25153. Behaviour inferred, not verified against a retail capture.
/// <para>
/// Evidence: classic-db ACID EventAI row 1522901 has the Vekniss Soldier (15229) cast Agro Drones 25152 on aggro. 25152 is a self aura whose periodic
/// trigger (1000 ms) casts 25153. Spell.dbc gives 25153 one DUMMY effect with the caster's position as target A and the friendly units around the
/// caster as target B, radius index 10 = 30 yd (SpellRadius.dbc), so the effect runs once on every friendly unit within 30 yd of the soldier. The name
/// says the drones aggro. The Vekniss Drone is creature entry 15300 (classic-db Updates 4689_TDB-0518_charm_immunity.sql names "Vekniss Drone" as
/// entry 15300). Each living Vekniss Drone the effect reaches that is not in combat starts attacking the soldier's current victim; a drone that is
/// dead, evading or already fighting is left alone. The radius is the spell's own, so the script chose none.
/// </para>
/// The attack goes through the drone's AI <c>AttackStart</c> (the call a taunt makes), or the creature system's when the drone has no AI, so the drone
/// gets threat on the victim, enters combat and its aggro hooks and normal assistance run.
/// </summary>
[SpellScript(25153)]
public sealed class AgroDronesEffectScript : ISpellScript
{
    /// <summary>Vekniss Drone, Temple of Ahn'Qiraj.</summary>
    public const uint VeknissDroneEntry = 15300;

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Target is not Creature { Entry: VeknissDroneEntry, IsAlive: true } drone || context.Caster.Combat.Victim is not { IsAlive: true } victim
            || drone.IsEvading || drone.Combat.IsInCombat || drone.Combat.Victim is not null)
        {
            return;
        }

        if (drone.AI is { } ai)
        {
            ai.AttackStart(victim);
        }
        else
        {
            drone.System?.AttackStart(drone, victim);
        }
    }
}
