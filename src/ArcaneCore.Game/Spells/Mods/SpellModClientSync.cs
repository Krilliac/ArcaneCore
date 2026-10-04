using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells.Mods;

/// <summary>
/// Keeps the client's copy of a player's modifiers current. After a mod is added or removed, for every set bit of its class
/// mask (0 to 63, ascending) one packet carries the sum of the player's mods of the same operation and type that have that bit
/// (vmangos Player::SendSpellMod): a removal sends the sums without the removed mod, 0 when none remain.
/// </summary>
internal sealed class SpellModClientSync(SpellModEngine engine)
{
    public void Attach() => engine.Changed += OnChanged;

    private void OnChanged(Player player, SpellMod mod, bool added)
    {
        if (!engine.Options.SendClientModifiers)
        {
            return;
        }

        IReadOnlyList<SpellMod> sameOp = engine.ModsOf(player, mod.Op);
        for (int bit = 0; bit < 64; bit++)
        {
            ulong flag = 1UL << bit;
            if ((mod.Mask & flag) == 0)
            {
                continue;
            }

            int sum = 0;
            foreach (SpellMod other in sameOp)
            {
                if (other.Type == mod.Type && (other.Mask & flag) != 0)
                {
                    sum += other.Value;
                }
            }

            player.Session.Send(SpellModPackets.OpcodeOf(mod.Type), SpellModPackets.Build(bit, mod.Op, sum));
        }
    }
}
