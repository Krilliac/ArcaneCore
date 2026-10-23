using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Totems;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestKit;

namespace ArcaneCore.Game.Tests.ClassSpells.Totems;

/// <summary>
/// Totem::Update / UnSummon (vmangos Objects/Totem.cpp:66-150) and Player::RemoveFromWorld
/// (Objects/Player.cpp:2208-2216): duration, owner death, owner leash, logout, kill, the totem's own aura on
/// its owner and party, and the spawn/despawn animation packets.
/// </summary>
public sealed class TotemLifecycleTests
{
    [Fact]
    public void TotemExpires_AtSummonSpellDuration_AndRemovesItsAuraFromTotemOwnerAndPartyCopies()
    {
        using var kit = new TotemTestKit(summonDurationMs: 2000);
        (Player shaman, _) = kit.AddPlayer(1, 0, 0);
        (Player member, _) = kit.AddPlayer(2, 10, 0);
        (Player outsider, _) = kit.AddPlayer(3, 12, 0);
        kit.Groups.Parties.Add([shaman.Guid, member.Guid]);
        kit.Cast(shaman, TotemTestKit.EarthTotemSummon);
        Creature totem = kit.Totems.GetTotem(shaman, TotemSlot.Earth)!;
        kit.Advance(1000);

        Assert.True(kit.Spells.HasAura(totem, TotemTestKit.PartyPassive));
        Assert.True(kit.Spells.HasAura(shaman, TotemTestKit.PartyPassive));
        Assert.True(kit.Spells.HasAura(member, TotemTestKit.PartyPassive));
        Assert.False(kit.Spells.HasAura(outsider, TotemTestKit.PartyPassive));

        kit.Advance(800);
        Assert.NotNull(kit.Totems.GetTotem(shaman, TotemSlot.Earth));

        kit.Advance(300);

        Assert.Null(kit.Totems.GetTotem(shaman, TotemSlot.Earth));
        Assert.Null(kit.Map.FindObject(totem.Guid));
        Assert.False(kit.Spells.HasAura(shaman, TotemTestKit.PartyPassive));
        Assert.False(kit.Spells.HasAura(member, TotemTestKit.PartyPassive));
    }

    [Fact]
    public void SummonedLife_FollowsTheCastDuration_NotTheRawSpellBase()
    {
        // vmangos Spell::EffectSummonTotem: pTotem->SetDuration(m_duration) (SpellEffects.cpp:4985).
        // m_duration is CalculateDuration, so a duration modifier must move the unsummon.
        using var kit = new TotemTestKit(summonDurationMs: 1000);
        kit.Spells.RegisterValueModifier(new ExtendDuration(4_000));
        (Player shaman, _) = kit.AddPlayer(1, 0, 0);
        kit.Cast(shaman, TotemTestKit.EarthTotemSummon);

        kit.Advance(1000);
        Assert.NotNull(kit.Totems.GetTotem(shaman, TotemSlot.Earth));

        kit.Advance(3900);
        Assert.NotNull(kit.Totems.GetTotem(shaman, TotemSlot.Earth));

        kit.Advance(200);
        Assert.Null(kit.Totems.GetTotem(shaman, TotemSlot.Earth));
    }

    [Fact]
    public void PartyAreaAura_ReachesTheOwnersSubGroup_OnlyThroughTheOwnerAwareResolver()
    {
        using var kit = new TotemTestKit();
        (Player shaman, _) = kit.AddPlayer(1, 0, 0);
        (Player member, _) = kit.AddPlayer(2, 10, 0);
        kit.Groups.Parties.Add([shaman.Guid, member.Guid]);
        kit.Cast(shaman, TotemTestKit.EarthTotemSummon);

        // Without the decorator the stock resolver does not know the totem, so nobody but the totem itself is reached.
        kit.Spells.Groups = kit.Groups;
        kit.Advance(1000);
        Assert.False(kit.Spells.HasAura(member, TotemTestKit.PartyPassive));
    }

    [Fact]
    public void OwnerDeath_Unsummons()
    {
        using var kit = new TotemTestKit();
        (Player shaman, _) = kit.AddPlayer(1, 0, 0);
        kit.Cast(shaman, TotemTestKit.EarthTotemSummon);

        shaman.Health = 0;
        kit.Advance(100);

        Assert.Empty(kit.Totems.Totems);
    }

    [Fact]
    public void CreatureOwnerDeath_DoesNotUnsummon_ButOwnerLeavingTheWorldDoes()
    {
        using var kit = new TotemTestKit();
        Creature owner = kit.Creatures.SpawnTemporary(Template(WolfEntry), 0, 0, 83.5f, 0);
        kit.World.RunTick(0);
        kit.Cast(owner, TotemTestKit.EarthTotemSummon);
        Assert.NotNull(kit.Totems.GetTotem(owner, TotemSlot.Earth));

        kit.Creatures.KillCreature(owner);
        kit.Advance(100);
        Assert.NotNull(kit.Totems.GetTotem(owner, TotemSlot.Earth));

        kit.Creatures.Despawn(owner);
        kit.Advance(100);
        Assert.Empty(kit.Totems.Totems);
    }

    [Fact]
    public void OwnerBeyondVisibilityDistance_Unsummons_UnlessTheLeashIsOff()
    {
        using var kit = new TotemTestKit();
        using var loose = new TotemTestKit(new TotemOptions { OwnerLeash = false });
        foreach (TotemTestKit k in new[] { kit, loose })
        {
            (Player shaman, _) = k.AddPlayer(1, 0, 0);
            k.Cast(shaman, TotemTestKit.EarthTotemSummon);
            k.Advance(100);
            Assert.NotNull(k.Totems.GetTotem(shaman, TotemSlot.Earth));

            shaman.Relocate(1000, 0, shaman.Z, 0, 0);
            k.Advance(100);
        }

        Assert.Empty(kit.Totems.Totems);
        Assert.Single(loose.Totems.Totems);
    }

    [Fact]
    public void OwnerLeavingTheMap_UnsummonsEveryTotem()
    {
        using var kit = new TotemTestKit();
        (Player shaman, _) = kit.AddPlayer(1, 0, 0);
        kit.Cast(shaman, TotemTestKit.FireTotemSummon);
        kit.Cast(shaman, TotemTestKit.WaterTotemSummon);
        Creature fire = kit.Totems.GetTotem(shaman, TotemSlot.Fire)!;

        kit.Map.RemovePlayer(shaman);

        Assert.Empty(kit.Totems.Totems);
        Assert.Null(kit.Map.FindObject(fire.Guid));
    }

    [Fact]
    public void KilledTotem_FreesItsSlot_AndIsRemovedOnTheNextUpdate()
    {
        using var kit = new TotemTestKit();
        (Player shaman, _) = kit.AddPlayer(1, 0, 0);
        kit.Cast(shaman, TotemTestKit.EarthTotemSummon);
        Creature totem = kit.Totems.GetTotem(shaman, TotemSlot.Earth)!;

        totem.Health = 0;
        kit.Advance(100);

        Assert.Null(kit.Totems.GetTotem(shaman, TotemSlot.Earth));
        Assert.Null(kit.Map.FindObject(totem.Guid));
        Assert.False(TotemQuery.IsTotem(totem));

        // The slot is usable again.
        kit.Cast(shaman, TotemTestKit.EarthTotemSummon);
        Assert.NotNull(kit.Totems.GetTotem(shaman, TotemSlot.Earth));
    }

    [Fact]
    public void FirstTick_OfTheListedPassives_IsImmediate_ButOrdinaryPassivesWaitOneAmplitude()
    {
        using var kit = new TotemTestKit();
        (Player shaman, _) = kit.AddPlayer(1, 0, 0);
        (Player other, _) = kit.AddPlayer(2, 20, 0);

        kit.Cast(shaman, 930007);   // Tremor Totem shape: passive 8145
        kit.Cast(other, 930008);    // an ordinary periodic passive

        Creature tremor = kit.Totems.GetTotem(shaman, TotemSlot.Earth)!;
        Creature slow = kit.Totems.GetTotem(other, TotemSlot.Earth)!;
        SpellAura immediate = kit.Spells.GetAuras(tremor).Single(h => h.Spell.Id == TotemTestKit.ImmediatePassive).Auras.OfType<SpellAura>().Single();
        SpellAura ordinary = kit.Spells.GetAuras(slow).Single(h => h.Spell.Id == TotemTestKit.SlowPassive).Auras.OfType<SpellAura>().Single();
        Assert.Equal(0, immediate.PeriodicTimer);
        Assert.Equal(3000, ordinary.PeriodicTimer);
    }

    [Fact]
    public void TotemWithoutASpell_IsSummonedAndDespawnedLikeAnyOther()
    {
        using var kit = new TotemTestKit();
        (Player shaman, _) = kit.AddPlayer(1, 0, 0);

        kit.Cast(shaman, 930009);   // Sentry Totem shape: creature with no totem_spell row

        Creature totem = kit.Totems.GetTotem(shaman, TotemSlot.Earth)!;
        Assert.Empty(kit.Spells.GetAuras(totem));
        kit.Cast(shaman, TotemTestKit.DestroyAll);
        Assert.Null(kit.Map.FindObject(totem.Guid));
    }

    [Fact]
    public void SentryTotem_RemovesOwnerAura6495_WhenKilledOrReplaced()
    {
        using var kit = new TotemTestKit();
        (Player shaman, _) = kit.AddPlayer(1, 0, 0);

        kit.Cast(shaman, TotemTestKit.SentryTotem);
        Creature sentry = kit.Totems.GetTotem(shaman, TotemSlot.Air)!;
        Assert.Equal(TotemTestKit.SentryEntry, sentry.Entry);
        Assert.True(kit.Spells.HasAura(shaman, TotemTestKit.SentryTotem));

        sentry.Health = 0;
        kit.Advance(100);

        Assert.Null(kit.Totems.GetTotem(shaman, TotemSlot.Air));
        Assert.False(kit.Spells.HasAura(shaman, TotemTestKit.SentryTotem));
    }

    [Fact]
    public void SpawnAnimation_ReachesObserversAfterTheyKnowTheTotem_AndDespawnAnimationBeforeItGoes_NeverATotemBar()
    {
        using var kit = new TotemTestKit();
        (Player shaman, _) = kit.AddPlayer(1, 0, 0);
        (Player watcher, FakeSession seen) = kit.AddPlayer(2, 5, 0);

        kit.Cast(shaman, TotemTestKit.EarthTotemSummon);
        Creature totem = kit.Totems.GetTotem(shaman, TotemSlot.Earth)!;
        var packets = new List<(WorldOpcode Opcode, byte[] Payload)>();

        kit.World.RunTick(50);
        kit.World.RunTick(50);
        kit.World.RunTick(50);
        List<ParsedBlock> blocks = DrainBlocks(seen, packets);

        Assert.Contains(blocks, b => b.Type is ObjectUpdateType.CreateObject or ObjectUpdateType.CreateObject2 && b.Guids[0] == totem.Guid.Value);
        (WorldOpcode _, byte[] spawn) = Assert.Single(packets, p => p.Opcode == WorldOpcode.SmsgGameobjectSpawnAnim);
        Assert.Equal(totem.Guid.Value, BitConverter.ToUInt64(spawn));
        Assert.Equal(8, spawn.Length);

        kit.Cast(shaman, TotemTestKit.DestroyAll);
        packets.Clear();
        DrainBlocks(seen, packets);
        (WorldOpcode _, byte[] despawn) = Assert.Single(packets, p => p.Opcode == WorldOpcode.SmsgGameobjectDespawnAnim);
        Assert.Equal(totem.Guid.Value, BitConverter.ToUInt64(despawn));
        Assert.DoesNotContain(packets, p => (ushort)p.Opcode is 0x412 or 0x413);
    }

    private sealed class ExtendDuration(int extraMs) : ISpellValueModifier
    {
        public int Modify(SpellValueKind kind, in SpellValueContext context, int value)
            => kind == SpellValueKind.Duration ? value + extraMs : value;
    }
}
