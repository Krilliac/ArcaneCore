using System.Text;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// Player SPELL_EFFECT_RESURRECT (18: percentage health/mana) and RESURRECT_NEW (113: flat
/// health, MiscValue mana), after vmangos SpellEffects.cpp:5228-5248 and 209-263.
/// Players receive an offer accepted through CMSG_RESURRECT_RESPONSE. Effect 113 also restores
/// an existing in-world current summoned pet; cached or persistent pet loading is separate.
/// </summary>
public sealed class ResurrectionEffects : ISpellHandlerModule
{
    /// <summary>SPELL_ATTR_EX3_NO_RES_TIMER (Rebirth), vmangos SpellDefines.h:947.</summary>
    public const uint NoResurrectionTimer = 0x10;

    public void Register(SpellSystem system)
    {
        system.RegisterEffect(SpellEffectName.Resurrect, Offer);
        system.RegisterEffect(SpellEffectName.ResurrectNew, Offer);
        system.RegisterEffectCheck(SpellEffectName.Resurrect, Check);
        system.RegisterEffectCheck(SpellEffectName.ResurrectNew, Check);
    }

    private static SpellCastResult Check(SpellEffectCheckContext context)
    {
        if ((context.Targets.Mask & (SpellCastTargetFlags.CorpseAlly | SpellCastTargetFlags.CorpseEnemy)) != 0)
        {
            return context.UnitTarget is { } target
                ? context.System.CheckResurrectionCorpseTarget(context.Caster, context.Spell, context.Targets, target, context.Triggered, context.Strict)
                : SpellCastResult.BadTargets;
        }

        return SpellCastResult.CastOk;
    }

    private static void Offer(SpellEffectContext context)
    {
        if (context.Effect.Effect == SpellEffectName.ResurrectNew && context.Target is Creature pet)
        {
            if (pet.System?.TryReviveCurrentPet(pet, context.Caster, unchecked((uint)context.Value), context.System) == true
                && pet.GetOwner() is { } owner)
            {
                // SpellEffects.cpp:240-256: remove every spell carrying Demonic Sacrifice's
                // override script, leaving the owner's other override-class scripts intact.
                uint[] sacrificeSpells = context.System.GetAuras(owner)
                    .Where(holder => holder.Auras.Any(aura => aura is { Type: AuraType.OverrideClassScripts, MiscValue: 2228 }))
                    .Select(holder => holder.Spell.Id).Distinct().ToArray();
                foreach (uint spell in sacrificeSpells)
                {
                    context.System.RemoveAuras(owner, spell);
                }
            }

            return;
        }

        if (context.Target is not Player player || player.Combat.DeathState == DeathState.Alive || !player.IsInWorld || context.Caster.Map is not { } map)
        {
            return;
        }

        uint health;
        uint mana;
        if (context.Effect.Effect == SpellEffectName.Resurrect)
        {
            health = Percentage(player.MaxHealth, context.Value);
            mana = Percentage(player.GetUInt32(UpdateFields.UnitFieldMaxpower1), context.Value);
        }
        else
        {
            health = (uint)Math.Max(0, context.Value);
            mana = (uint)Math.Max(0, context.Effect.MiscValue);
        }

        Unit caster = context.Caster;
        var request = new ResurrectionRequest(caster.Guid,
            new TeleportDestination(map.MapId, caster.X, caster.Y, caster.Z, caster.Orientation), map.InstanceId,
            health, mana, caster is Player);
        if (!PlayerResurrection.TryOffer(player, request))
        {
            return;
        }

        string name = caster is Creature creature ? creature.Template.Name : string.Empty;
        bool sickness = caster is Creature healer && (healer.NpcFlags & (uint)NpcFlags.SpiritHealer) != 0;
        player.Session.Send(WorldOpcode.SmsgResurrectRequest, BuildRequest(caster.Guid, name, sickness,
            delayed: (context.Spell.AttributesEx3 & NoResurrectionTimer) == 0));
    }

    private static uint Percentage(uint maximum, int percent)
        => (uint)Math.Min(uint.MaxValue, (ulong)maximum * (uint)Math.Max(0, percent) / 100);

    /// <summary>
    /// Build 5875: unpacked GUID, u32 UTF-8 name length including terminator, CString, sickness
    /// byte and delay byte (vmangos Server/Packets/Spell.cpp:641; cmangos Player.cpp:18892).
    /// Player casters use an empty name. wow_messages currently omits the second trailing byte.
    /// </summary>
    public static byte[] BuildRequest(ObjectGuid caster, string name, bool sickness, bool delayed)
    {
        var writer = new PacketWriter();
        writer.WriteUInt64(caster.Value);
        writer.WriteUInt32((uint)Encoding.UTF8.GetByteCount(name) + 1);
        writer.WriteCString(name);
        writer.WriteByte(sickness ? (byte)1 : (byte)0);
        writer.WriteByte(delayed ? (byte)1 : (byte)0);
        return writer.ToArray();
    }
}
