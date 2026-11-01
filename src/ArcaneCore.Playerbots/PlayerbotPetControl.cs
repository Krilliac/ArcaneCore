using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// What a managed bot tells its pet beyond attack and follow: a pet's harmful spells (a hunter pet's Growl and Claw, a demon's Firebolt) start with
/// autocast off (<c>ACT_DECIDE</c>, Pet.cpp:1887-1975), so a bot that only sends the attack command leaves them uncast. The bot turns them on the way
/// a client does, with CMSG_PET_SPELL_AUTOCAST, which <c>PetController.HandleAutocast</c> validates. Spells are told apart by id: a spell name is
/// shared by every rank and by unrelated spells, an id is one spell.
/// <para>Thread affinity: the world thread that runs the bot's think. Allocation: none while every harmful spell is already on.</para>
/// </summary>
internal static class PlayerbotPetControl
{
    /// <summary>
    /// The ids of the pet's known, castable, harmful spells whose autocast is off, in ascending order. A passive spell, a spell the store does not
    /// know and a beneficial spell (which PetAI casts on the pet or an ally on its own terms) are left alone.
    /// </summary>
    internal static List<uint> HarmfulSpellsWithAutocastOff(CharmInfo charm, Func<uint, SpellInfo?> lookup)
    {
        ArgumentNullException.ThrowIfNull(charm);
        ArgumentNullException.ThrowIfNull(lookup);
        List<uint> off = [];
        foreach ((uint spellId, ActionType state) in charm.SpellStates)
        {
            if (state == ActionType.Disabled && lookup(spellId) is { IsPassive: false, IsPositive: false })
            {
                off.Add(spellId);
            }
        }

        off.Sort();
        return off;
    }

    /// <summary>
    /// Turn autocast on for every harmful spell of the bot's current pet that has it off. Returns how many requests were sent. The handler applies each
    /// at once, so the next think finds nothing to do; a pet the owner cannot control (dead, gone, not in the map) is skipped.
    /// </summary>
    internal static int EnableHarmfulAutocast(WorldSession session, Player player, SpellSystem spells)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(spells);
        if (player.PetGuid.IsEmpty || player.Map?.FindObject(player.PetGuid) is not Creature { IsAlive: true } pet
            || pet.OwnerGuid != player.Guid || pet.Summon?.Charm is not { } charm)
        {
            return 0;
        }

        List<uint> off = HarmfulSpellsWithAutocastOff(charm, spells.Store.Get);
        int sent = 0;
        foreach (uint spellId in off)
        {
            var writer = new PacketWriter(13);
            writer.WriteUInt64(pet.Guid.Value);
            writer.WriteUInt32(spellId);
            writer.WriteByte(1);
            if (session.TryManagedAction(WorldOpcode.CmsgPetSpellAutocast, writer.ToArray()))
            {
                sent++;
            }
        }

        return sent;
    }
}
