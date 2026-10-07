using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Totems;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Spells.Totems;
using ArcaneCore.World.Tests.Creatures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.ClassSpells.Totems;

public sealed class GroundingTotemWorldTests
{
    private const uint Summon = 970101;
    private const uint Grounding = 8179;
    private const uint HostileSpell = 970102;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PartyProtectionRedirectsThroughProductionSpellPipeline_AndConsumesSource(bool damaging)
    {
        var template = new CreatureTemplate
        {
            Entry = TotemTestServices.TotemEntry, Name = "Grounding Totem", MinLevel = 1, MaxLevel = 1,
            DisplayIds = [903], Faction = 35, MinLevelHealth = 5, MaxLevelHealth = 5, AIName = "TotemAI", CreatureType = 11,
        };
        CreatureTestStore.Current.Value = new CreatureTestContext(new CreatureContent([template], [], [], [], []));
        WorldTestHost host;
        try { host = WorldTestHost.Start(); }
        finally { CreatureTestStore.Current.Value = null; }
        await using (host)
        await using (WorldTestClient client = await host.EnterWorldAsync("GROUNDING", "Grounding"))
        {
            await client.CollectAsync();
            Creature? totem = null;
            Creature? attacker = null;
            uint before = 0;
            await host.OnWorldAsync(() =>
            {
                Player owner = host.World.FindOnlinePlayer("Grounding")!;
                SpellSystem spells = host.WorldServices.GetRequiredService<SpellFeature>().System;
                spells.CombatRules = SpellCombatRules.Neutral;
                spells.Store = new SpellStore(
                [
                    .. spells.Store.All,
                    new SpellInfo
                    {
                        Id = Summon, Name = "Grounding Totem Summon", RangeIndex = SpellConstants.RangeIndexSelfOnly,
                        Duration = new SpellDuration(60_000, 0, 60_000),
                        Effects = [new SpellEffectInfo
                        {
                            Effect = SpellEffectName.SummonTotemSlot4, BasePoints = 4, BaseDice = 1, DieSides = 1,
                            TargetA = (SpellImplicitTarget)43, MiscValue = (int)TotemTestServices.TotemEntry,
                        }],
                    },
                    new SpellInfo
                    {
                        Id = TotemTestServices.TotemPassive, Name = "Grounding Totem Passive",
                        Effects = [new SpellEffectInfo { Effect = SpellEffectName.TriggerSpell, TriggerSpell = Grounding, TargetA = SpellImplicitTarget.UnitCaster }],
                    },
                    new SpellInfo
                    {
                        Id = Grounding, Name = "Grounding Protection", Attributes = SpellAttributes.Passive,
                        Duration = new SpellDuration(-1, 0, -1), ProcCharges = 1,
                        Effects = [new SpellEffectInfo
                        {
                            Effect = SpellEffectName.ApplyAreaAuraParty, AuraType = AuraType.SpellMagnet,
                            TargetA = SpellImplicitTarget.UnitCaster, Radius = 30,
                        }],
                    },
                    new SpellInfo
                    {
                        Id = HostileSpell, Name = "Incoming Magic", School = SpellSchool.Fire,
                        DamageClass = SpellDamageClass.Magic, RangeIndex = 4, Range = new SpellRange(0, 30),
                        TargetCreatureType = 1 << 6, // humanoid; 8179 explicitly allows Grounding to intercept it
                        Duration = new SpellDuration(3000, 0, 3000),
                        Effects = [new SpellEffectInfo
                        {
                            Effect = damaging ? SpellEffectName.SchoolDamage : SpellEffectName.ApplyAura,
                            AuraType = AuraType.Dummy, BasePoints = 24, BaseDice = 1, DieSides = 1,
                            TargetA = SpellImplicitTarget.UnitEnemy,
                        }],
                    },
                ], [], []);
                attacker = host.WorldServices.GetRequiredService<CreatureWorldFeature>().GetOrCreateSystem(owner.Map!)
                    .SpawnTemporary(template with { Entry = 970103, Name = "Enemy Mage", Faction = 14, AIName = "NullAI", CreatureType = 7 },
                        owner.X + 8, owner.Y, owner.Z, 0);
                Assert.Equal(SpellCastResult.CastOk, spells.CastSpell(owner, Summon, SpellCastTargets.ForSelf(), true));
                totem = Assert.IsType<Creature>(host.WorldServices.GetRequiredService<TotemFeature>().System!.GetTotem(owner, TotemSlot.Air));
                before = owner.Health;
                Assert.Equal(owner.Guid.Value, totem.GetUInt64(UpdateFields.UnitFieldSummonedby));
                Assert.True(spells.HasAura(totem, Grounding));
            });
            await host.WaitForWorldAsync(() => host.WorldServices.GetRequiredService<SpellFeature>().System
                .HasAura(host.World.FindOnlinePlayer("Grounding")!, Grounding), "grounding party aura propagation");
            await client.CollectAsync();
            await host.OnWorldAsync(() =>
            {
                Player owner = host.World.FindOnlinePlayer("Grounding")!;
                SpellSystem spells = host.WorldServices.GetRequiredService<SpellFeature>().System;
                Assert.Equal(SpellCastResult.CastOk, spells.CastSpell(attacker!, HostileSpell, SpellCastTargets.ForUnit(owner.Guid), true));
                Assert.Equal(before, owner.Health);
                Assert.False(spells.HasAura(owner, Grounding));
                Assert.False(spells.HasAura(totem!, Grounding));
                if (!damaging)
                {
                    Assert.True(totem!.IsAlive);
                    // Grounding consumes the redirect before hit resolution; the intrinsic
                    // totem immunity then rejects the hostile aura without killing the totem.
                    Assert.False(spells.HasAura(totem, HostileSpell));
                }
            });
            byte[] go;
            do { go = await client.ReadUntilAsync(WorldOpcode.SmsgSpellGo); }
            while (SpellId(go) != HostileSpell);
            AssertRedirectedGo(go, totem!.Guid, damaging);
            if (damaging)
            {
                await host.WaitForWorldAsync(() => !totem.IsInWorld, "redirected damage kills and unsummons grounding totem");
                await host.OnWorldAsync(() => Assert.Null(host.WorldServices.GetRequiredService<SpellFeature>().System.GetState(totem.Guid)));
            }
            else
            {
                await host.OnWorldAsync(() =>
                {
                    Assert.True(totem.IsInWorld);
                    Player owner = host.World.FindOnlinePlayer("Grounding")!;
                    SpellSystem spells = host.WorldServices.GetRequiredService<SpellFeature>().System;
                    spells.CastSpell(attacker!, HostileSpell, SpellCastTargets.ForUnit(owner.Guid), true);
                    Assert.True(spells.HasAura(owner, HostileSpell)); // consumed protection is not regenerated next tick
                });
            }
        }
    }

    private static uint SpellId(byte[] payload)
    {
        var reader = new PacketReader(payload);
        _ = reader.ReadPackedGuid();
        _ = reader.ReadPackedGuid();
        return reader.ReadUInt32();
    }

    private static void AssertRedirectedGo(byte[] payload, ObjectGuid guid, bool damaging)
    {
        var reader = new PacketReader(payload);
        _ = reader.ReadPackedGuid();
        _ = reader.ReadPackedGuid();
        Assert.Equal(HostileSpell, reader.ReadUInt32());
        _ = reader.ReadUInt16();
        Assert.Equal(damaging ? 1 : 0, reader.ReadByte());
        if (damaging)
        {
            Assert.Equal(guid.Value, reader.ReadUInt64());
            Assert.Equal(0, reader.ReadByte());
        }
        else
        {
            Assert.Equal(1, reader.ReadByte());
            Assert.Equal(guid.Value, reader.ReadUInt64());
            Assert.Equal((byte)SpellMissInfo.Immune2, reader.ReadByte());
        }
        Assert.Equal(guid, SpellCastTargets.Read(ref reader).Unit);
    }
}
