using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Characters.Pets;
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

    private const uint CallPetSpell = 991002;

    /// <summary>What the owner's session received, in order: a create block of a GUID, a values block of a GUID with its UNIT_FIELD_SUMMON, or the pet bar.</summary>
    private static List<string> OwnerTimeline(FakeSession session, Player owner, Creature pet)
    {
        var events = new List<string>();
        foreach ((WorldOpcode opcode, byte[] payload) in session.Sent)
        {
            if (opcode == WorldOpcode.SmsgPetSpells)
            {
                events.Add("petspells");
            }
            else if (opcode == WorldOpcode.SmsgUpdateObject)
            {
                foreach (ParsedBlock block in CreatureTestKit.ParseUpdateObject(payload))
                {
                    if (block.Type is ObjectUpdateType.CreateObject or ObjectUpdateType.CreateObject2 && block.Guids.Contains(pet.Guid.Value))
                    {
                        events.Add("create-pet");
                    }
                    else if (block.Type == ObjectUpdateType.Values && block.Guids.Contains(owner.Guid.Value)
                        && block.Values.TryGetValue(UpdateFields.UnitFieldSummon, out uint low) && low == (uint)(pet.Guid.Value & 0xFFFFFFFFu))
                    {
                        events.Add("owner-summon");
                    }
                }
            }
        }

        return events;
    }

    [Fact]
    public void SummonImp_DeliversThePetCreateBlockAndTheOwnersSummonFieldBeforeThePetBar()
    {
        using PetTestKit kit = ImpKit();
        (Player owner, FakeSession session) = kit.AddPlayer(1);
        owner.Level = 10;
        session.Clear();

        kit.Cast(owner, SummonImpSpell);

        Creature imp = Assert.Single(kit.Creatures.Creatures, c => c.IsPet);
        List<string> events = OwnerTimeline(session, owner, imp);
        Assert.Contains("create-pet", events);
        Assert.Contains("owner-summon", events);
        int bar = events.IndexOf("petspells");
        Assert.True(bar > events.IndexOf("create-pet"), $"pet bar before the pet create block: {string.Join(", ", events)}");
        Assert.True(bar > events.IndexOf("owner-summon"), $"pet bar before UNIT_FIELD_SUMMON: {string.Join(", ", events)}");
    }

    [Fact]
    public async Task CallPet_DeliversThePetCreateBlockAndTheOwnersSummonFieldBeforeThePetBar()
    {
        using PetTestKit kit = new([Spell(CallPetSpell, Effect(SpellEffectName.SummonPet, 0, misc: 0))]);
        (Player owner, FakeSession session) = kit.AddPlayer(1);
        owner.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        kit.Service.LoadPersistence = (_, _) => Task.FromResult<PersistentPetSnapshot?>(
            new(1, 801, PetEntry, 5, 0, 50, 0, 0, 1, [1, 2, 3], []));
        await kit.Service.ReadCurrentPetAsync(owner);
        session.Clear();

        kit.Cast(owner, CallPetSpell);

        Creature pet = Assert.Single(kit.Creatures.Creatures);
        List<string> events = OwnerTimeline(session, owner, pet);
        int bar = events.IndexOf("petspells");
        Assert.True(bar >= 0, "no pet bar sent");
        Assert.True(events.IndexOf("create-pet") is >= 0 and var create && create < bar, $"pet bar before the pet create block: {string.Join(", ", events)}");
        Assert.True(events.IndexOf("owner-summon") is >= 0 and var summon && summon < bar, $"pet bar before UNIT_FIELD_SUMMON: {string.Join(", ", events)}");
    }

    [Fact]
    public void SummonImp_WritesThePetNumberToUnitFieldPetnumber()
    {
        using PetTestKit kit = ImpKit();
        (Player owner, _) = kit.AddPlayer(1);
        owner.Level = 10;

        kit.Cast(owner, SummonImpSpell);

        Creature imp = Assert.Single(kit.Creatures.Creatures, c => c.IsPet);
        uint number = imp.Summon!.Charm!.PetNumber;
        Assert.NotEqual(0u, number);
        Assert.Equal(number, imp.GetUInt32(UpdateFields.UnitFieldPetnumber));
        Assert.Equal(number, imp.Guid.Entry); // the pet number is also the entry part of the HIGHGUID_PET guid
    }

    [Fact]
    public async Task CallPet_WritesTheStablePetNumberToTheField()
    {
        using PetTestKit kit = new([Spell(CallPetSpell, Effect(SpellEffectName.SummonPet, 0, misc: 0))]);
        (Player owner, _) = kit.AddPlayer(1);
        owner.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        kit.Service.LoadPersistence = (_, _) => Task.FromResult<PersistentPetSnapshot?>(
            new(1, 802, PetEntry, 5, 0, 50, 0, 0, 1, [1, 2, 3], []));
        await kit.Service.ReadCurrentPetAsync(owner);

        kit.Cast(owner, CallPetSpell);

        Creature pet = Assert.Single(kit.Creatures.Creatures);
        Assert.Equal(802u, pet.GetUInt32(UpdateFields.UnitFieldPetnumber));
    }

    [Fact]
    public void AGuardianKeepsAZeroPetNumberField()
    {
        using PetTestKit kit = ImpKit();
        (Player owner, _) = kit.AddPlayer(1);

        kit.Cast(owner, GuardianSpell);

        Creature guardian = Assert.Single(kit.Creatures.Creatures);
        Assert.Equal(0u, guardian.GetUInt32(UpdateFields.UnitFieldPetnumber));
    }
}
