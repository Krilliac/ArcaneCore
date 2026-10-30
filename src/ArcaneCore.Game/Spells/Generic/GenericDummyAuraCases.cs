using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Spells.Generic;

/// <summary>
/// The generic SPELL_AURA_DUMMY cases of vmangos <c>Aura::HandleAuraDummy</c> (SpellAuras.cpp:1625-2215) and <c>Aura::PeriodicDummyTick</c>
/// (SpellAuras.cpp:6395-6560), the ones that touch only the target, the creature that cast them and generic spells. Each spell is one case of
/// <see cref="SpellSystem.RegisterDummyAuraHandler"/> and, when vmangos makes the aura periodic, <see cref="SpellSystem.RegisterPeriodicDummyAura"/>.
/// <para>
/// Discovered like every <see cref="ISpellHandlerModule"/>. Thread affinity: the world thread (aura handlers and map updaters); delayed work
/// runs through the map's unit scheduler (<c>m_Events.AddLambdaEventAtOffset</c> in vmangos). Nothing here is on a per-frame path: the ticks run
/// once per second at the fastest and allocate nothing.
/// </para>
/// </summary>
public sealed class GenericDummyAuraModule : ISpellHandlerModule
{
    // Emotes.dbc ids (vmangos SharedDefines.h:485-536).
    public const uint EmoteOneShotCheer = 4, EmoteStateDance = 10, EmoteOneShotLaugh = 11, EmoteStateSleep = 12, EmoteOneShotChicken = 19,
        EmoteOneShotApplaud = 21, EmoteOneShotDance = 94;

    public const uint SleepUntilCancelled = 6606, SleepUntilCancelledB = 14915, SleepUntilCancelledC = 16093;
    public const uint PartyFever = 6758, PartyTime = 8067, NetGuard = 11920, ForceCreateElementalTotem = 13910;
    public const uint MelodiousRaptureVisual = 21051, FrostwolfAura = 21827, AlteracRamAura = 21863, IntoxicatingVenom = 24596;
    public const uint BabyMurloc = 24984, BabyMurlocDance = 25165, Cologne = 26681, Perfume = 26682, PermanentFeignDeath = 29266;
    public const uint CurseOfTheBleakheart = 6946, ForsakenSkills = 7054, HauntingSpirits = 7057, HauntingPhantoms = 16336, GargoyleStoneform = 29153;

    public const uint SummonElementalTotem = 13909, DetectAmore = 26802, Disarm = 8379, FallDown = 6869, BleakheartCurse = 6945;
    public const uint SummonHauntingSpirit = 7067, SummonSpitefulPhantom = 16334, SummonWrathPhantom = 16335;
    public const int NetGuardThreatPercent = -50;
    public const uint ForsakenSkillsFirst = 7038, ForsakenSkillsPeriodMs = 30_000;

    /// <summary>Delay before the sleep visuals take hold (vmangos SpellAuras.cpp:2043 <c>AddLambdaEventAtOffset(..., 500)</c>).</summary>
    public const uint SleepDelayMs = 500;

    /// <summary>MAX_UNIT_STAND_STATE: the stand state the gargoyle stoneform uses (taken from a sniff, vmangos instance_naxxramas.cpp:2095).</summary>
    public const StandState StoneformStandState = StandState.Custom;

    private static readonly uint[] MovingPartyEmotes = [EmoteOneShotApplaud, EmoteOneShotCheer, EmoteOneShotChicken, EmoteOneShotLaugh];

    private static readonly uint[] StandingPartyEmotes = [EmoteOneShotApplaud, EmoteOneShotCheer, EmoteOneShotChicken, EmoteOneShotLaugh, EmoteOneShotDance];

    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);

        // Self Visual - Sleep Until Cancelled (DND), SpellAuras.cpp:2033-2048: half a second after the aura lands the unit sleeps. Removal does nothing.
        foreach (uint spellId in new[] { SleepUntilCancelled, SleepUntilCancelledB, SleepUntilCancelledC })
        {
            system.RegisterDummyAuraHandler(spellId, static (_, holder, _, apply) =>
            {
                if (apply)
                {
                    Unit target = holder.Target;
                    AuraEvents.After(target, SleepDelayMs, () =>
                    {
                        target.SetUInt32(UpdateFields.UnitNpcEmotestate, EmoteStateSleep);
                        AuraEvents.SetStandState(target, StandState.Sleep);
                    });
                }
            });
        }

        // Party Fever (SpellAuras.cpp:1659-1666): a player out of combat starts to dance.
        system.RegisterDummyAuraHandler(PartyFever, static (_, holder, _, apply) =>
        {
            if (apply && holder.Target is Player player && (player.UnitFlags & UnitFlags.InCombat) == 0)
            {
                AuraEvents.Emote(player, EmoteStateDance);
            }
        });

        // Party Time! (SpellAuras.cpp:1668-1673, 6435-6445): every ten seconds a random one-shot emote, dancing only for a unit that stands still.
        system.RegisterPeriodicDummyAura(PartyTime, 10_000, static (spells, holder, _) =>
        {
            Unit target = holder.Target;
            uint[] emotes = (target.Movement.Flags & MovementFlags.MaskMoving) != 0 ? MovingPartyEmotes : StandingPartyEmotes;
            AuraEvents.Emote(target, emotes[spells.Random.Next(emotes.Length)]);
        });

        // Murloc Critter Dance (SpellAuras.cpp:1784-1789): the unit dances while the aura lands.
        foreach (uint spellId in new[] { BabyMurloc, BabyMurlocDance })
        {
            system.RegisterDummyAuraHandler(spellId, static (_, holder, _, apply) =>
            {
                if (apply)
                {
                    AuraEvents.Emote(holder.Target, EmoteStateDance);
                }
            });
        }

        // Net Guard (SpellAuras.cpp:1686-1691): a creature caster loses half of its threat on the target.
        system.RegisterDummyAuraHandler(NetGuard, static (_, holder, _, apply) =>
        {
            if (apply && AuraEvents.CasterOf(holder) is Creature caster)
            {
                caster.Combat.Threat.ModifyThreatPercent(holder.Target, NetGuardThreatPercent);
            }
        });

        // Force Create Elemental Totem (SpellAuras.cpp:1718-1730): the caster speaks (script text 5428) and the player casts Create Elemental Totem.
        system.RegisterDummyAuraHandler(ForceCreateElementalTotem, static (spells, holder, _, apply) =>
        {
            if (apply && holder.Target is Player player)
            {
                if (AuraEvents.CasterOf(holder) is Creature caster)
                {
                    caster.System?.SayText(caster, 5428, player);
                }

                spells.CastSpell(player, SummonElementalTotem, SpellCastTargets.ForSelf(), triggered: false);
            }
        });

        // Melodious Rapture Visual (SpellAuras.cpp:1743-1749, 6446-6457): every two seconds the rat that lost its follow movement goes away.
        system.RegisterPeriodicDummyAura(MelodiousRaptureVisual, 2000, static (_, holder, _) =>
        {
            if (holder.Target is Creature rat && rat.Motion.CurrentType != MovementGeneratorType.Follow)
            {
                WorldDummyCasts.Despawn(rat, 1);
            }
        });

        // Frostwolf Aura DND and Alterac Ram Aura DND (SpellAuras.cpp:1744-1750, 1948-1956, 6475-6512): the creature that cast it follows the player,
        // and goes away with the aura or once the player is dead or 50 yards off.
        foreach (uint spellId in new[] { FrostwolfAura, AlteracRamAura })
        {
            system.RegisterDummyAuraHandler(spellId, static (_, holder, _, apply) =>
            {
                if (!apply && AuraEvents.CasterOf(holder) is Creature { IsAlive: true } mount)
                {
                    WorldDummyCasts.Despawn(mount, 2000);
                }
            });
            system.RegisterPeriodicDummyAura(spellId, 2000, TickMountAura);
        }

        // Intoxicating Venom (SpellAuras.cpp:1775-1783, 6513-6523): every second, in combat, 7% Disarm and 7% Fall Down.
        system.RegisterPeriodicDummyAura(IntoxicatingVenom, 1000, static (spells, holder, _) =>
        {
            Unit target = holder.Target;
            if ((target.UnitFlags & UnitFlags.InCombat) == 0)
            {
                return;
            }

            int roll = spells.Random.Next(100);
            if (roll < 7)
            {
                spells.CastSpellTriggeredByAura(target, Disarm, SpellCastTargets.ForSelf(), holder.Spell);
            }
            else if (roll < 14)
            {
                spells.CastSpellTriggeredByAura(target, FallDown, SpellCastTargets.ForSelf(), holder.Spell);
            }
        });

        // Love is in the Air - Cologne and Perfume (SpellAuras.cpp:2065-2090): Detect Amore follows the aura one tick later, in either direction.
        foreach (uint spellId in new[] { Cologne, Perfume })
        {
            system.RegisterDummyAuraHandler(spellId, static (spells, holder, _, apply) =>
            {
                if (holder.Target is not Player player)
                {
                    return;
                }

                if (apply)
                {
                    AuraEvents.After(player, 1, () => spells.CastSpell(player, DetectAmore, SpellCastTargets.ForSelf(), triggered: true));
                }
                else
                {
                    AuraEvents.After(player, 1, () => spells.RemoveAuras(player, DetectAmore));
                }
            });
        }

        // Permanent Feign Death (SpellAuras.cpp:2103-2112): a creature feigns death for as long as the aura lasts.
        system.RegisterDummyAuraHandler(PermanentFeignDeath, static (spells, holder, _, apply) =>
        {
            if (holder.Target is Creature creature)
            {
                spells.SetCreatureFeignDeath(creature, apply);
            }
        });

        // Forsaken Skills (SpellAuras.cpp:1471-1480, 6409-6428): the vmangos spell_effect_mod row makes the aura a PERIODIC_TRIGGER_SPELL every
        // 30 seconds; the tick casts one of the Forsaken Skill spells 7038-7051 or 7053 at random (7052 is replaced by 7053).
        system.RegisterPeriodicDummyAura(ForsakenSkills, ForsakenSkillsPeriodMs, static (spells, holder, _) =>
        {
            uint skill = ForsakenSkillsFirst + (uint)spells.Random.Next(15);
            spells.CastSpellTriggeredByAura(holder.Target, skill == 7052 ? 7053 : skill, SpellCastTargets.ForSelf(), holder.Spell);
        });

        // Curse of the Bleakheart, Haunting Spirits and Haunting Phantoms (vmangos felwood.cpp:643-665, instance_shadowfang_keep.cpp:353-369,
        // stratholme.cpp:656-678, aura scripts): a five second tick, 5% of which casts the follow-up spell on the target.
        system.RegisterPeriodicDummyAura(CurseOfTheBleakheart, 5000, static (spells, holder, _) =>
        {
            if (spells.Random.Next(100) < 5)
            {
                spells.CastSpellTriggeredByAura(holder.Target, BleakheartCurse, SpellCastTargets.ForSelf(), holder.Spell);
            }
        });
        system.RegisterPeriodicDummyAura(HauntingSpirits, 5000, static (spells, holder, _) =>
        {
            if (spells.Random.Next(100) < 5)
            {
                spells.CastSpell(holder.Target, SummonHauntingSpirit, SpellCastTargets.ForSelf(), triggered: true);
            }
        });
        system.RegisterPeriodicDummyAura(HauntingPhantoms, 5000, static (spells, holder, _) =>
        {
            if (spells.Random.Next(100) < 5)
            {
                spells.CastSpell(holder.Target, spells.Random.Next(2) != 0 ? SummonSpitefulPhantom : SummonWrathPhantom, SpellCastTargets.ForSelf(), triggered: true);
            }
        });

        // Gargoyle Stoneform Visual (vmangos instance_naxxramas.cpp:2087-2104, aura script): the gargoyle is frozen into stone and cannot be
        // selected; both go with the aura.
        system.RegisterDummyAuraHandler(GargoyleStoneform, static (_, holder, _, apply) =>
        {
            Unit target = holder.Target;
            AuraEvents.SetStandState(target, apply ? StoneformStandState : StandState.Stand);
            target.UnitFlags = apply ? target.UnitFlags | UnitFlags.NotSelectable : target.UnitFlags & ~UnitFlags.NotSelectable;
        });
    }

    /// <summary>The tick of the Frostwolf and Alterac Ram auras (vmangos SpellAuras.cpp:6475-6512): leash the mount to the player or end the aura.</summary>
    private static void TickMountAura(SpellSystem spells, SpellAuraHolder holder, SpellAura aura)
    {
        if (holder.Target is not Player player)
        {
            return;
        }

        if (AuraEvents.CasterOf(holder) is not Creature mount)
        {
            holder.Duration = 1; // the mount is gone
            return;
        }

        if (!player.IsAlive || !mount.IsAlive || !SpellSystem.IsWithinLeash(mount, player, 50.0f))
        {
            WorldDummyCasts.Despawn(mount, 1000);
            holder.Duration = 1;
            return;
        }

        if (mount.Motion.CurrentType is MovementGeneratorType.Idle or MovementGeneratorType.Random or MovementGeneratorType.Waypoint)
        {
            mount.Motion.MoveFollow(player, 3.0f, 0f);
        }
    }
}

/// <summary>The unit helpers of the generic DUMMY aura cases.</summary>
internal static class AuraEvents
{
    /// <summary>
    /// vmangos <c>m_Events.AddLambdaEventAtOffset(action, delayMs)</c> for a unit: the map's unit scheduler runs it after the delay (at once when the
    /// unit is not on a map).
    /// </summary>
    public static void After(Unit unit, uint delayMs, Action action)
    {
        if (unit.Map?.FindUpdater<MapUnitControl>() is { } control)
        {
            control.Schedule(unit, delayMs, action);
        }
        else
        {
            action();
        }
    }

    /// <summary>The unit that cast the aura, when it is on the target's map.</summary>
    public static Unit? CasterOf(SpellAuraHolder holder) => holder.Target.Map?.FindObject(holder.CasterGuid) as Unit;

    /// <summary>vmangos Unit::HandleEmoteCommand (Unit.cpp:1848-1854): SMSG_EMOTE to the unit's own client and everyone who sees it.</summary>
    public static void Emote(Unit unit, uint emote)
        => SpellSystem.SendToSet(unit, WorldOpcode.SmsgEmote, CreatureChatPackets.BuildEmote(emote, unit.Guid), includeSelf: true);

    /// <summary>vmangos Unit::SetStandState: a player's client is told, a creature's field changes.</summary>
    public static void SetStandState(Unit unit, StandState state)
    {
        if (unit is Player player)
        {
            player.SetStandState(state);
        }
        else
        {
            unit.StandState = state;
        }
    }
}
