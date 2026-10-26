using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Pets;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Pets.PetTestKit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Pets;

/// <summary>
/// The pet fixes of the "pet data, frame and runaway" change: the shipped create spells reach a summoned imp (A), the owner receives the
/// pet's create block before SMSG_PET_SPELLS (B), the pet number field (C) and the loyalty runaway (E). Spell ids are the retail ones:
/// Summon Imp 688, Firebolt rank 1 3110 (classic-db petcreateinfo_spell row 416).
/// </summary>
public sealed class PetFixesTests
{
    private const uint SummonImpSpell = 688;
    private const uint FireboltRank1 = 3110;

    private static IEnumerable<SpellInfo> ImpSpells() =>
    [
        Spell(SummonImpSpell, Effect(SpellEffectName.SummonPet, 0, (SpellImplicitTarget)32, misc: (int)ImpEntry)) with { StartRecoveryCategory = 0, StartRecoveryTime = 0 },
        Spell(FireboltRank1, Effect(SpellEffectName.SchoolDamage, 8, SpellImplicitTarget.UnitEnemy)) with { RangeIndex = 4, Range = new SpellRange(0, 30) },
    ];

    private static PetContent ImpContent() => new([], [new PetCreateSpells(ImpEntry, [FireboltRank1])]);

    private static PetTestKit ImpKit()
    {
        var kit = new PetTestKit(ImpSpells(), ImpContent());
        kit.Service.InstallDemons(kit.Spells.System);
        return kit;
    }

    [Fact]
    public void SummonImp_WithTheShippedCreateSpellRow_LearnsFireboltAndListsOneSpellInThePetBar()
    {
        using PetTestKit kit = ImpKit();
        (Player owner, FakeSession session) = kit.AddPlayer(1);
        owner.Level = 10;
        session.Clear();

        kit.Cast(owner, SummonImpSpell);

        Creature imp = Assert.Single(kit.Creatures.Creatures, c => c.IsPet);
        Assert.Equal([(FireboltRank1, ActionType.Disabled)], imp.Summon!.Charm!.SpellStates.Select(s => (s.Key, s.Value)));
        var r = new PacketReader(Assert.Single(Packets(session, WorldOpcode.SmsgPetSpells)));
        r.Skip(8 + 4 + 4 + 40);
        Assert.Equal(1, r.ReadByte());
        Assert.Equal(ActionButton.Make(FireboltRank1, ActionType.Disabled).Packed, r.ReadUInt32());
    }
}
