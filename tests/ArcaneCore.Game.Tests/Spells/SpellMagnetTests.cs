using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Reputation;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Reputation;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

public sealed class SpellMagnetTests
{
    private const uint Protection = 970001;
    private const uint Bolt = 970002;

    private static SpellInfo Protect(uint charges = 1) => Spell(Protection,
        Effect(SpellEffectName.ApplyAura, 1, SpellImplicitTarget.UnitFriend, AuraType.SpellMagnet)) with
    {
        ProcCharges = charges, Duration = new SpellDuration(60_000, 0, 60_000),
        RangeIndex = 4, Range = new SpellRange(0, 30), StartRecoveryCategory = 0, StartRecoveryTime = 0,
    };

    private static SpellInfo Attack(params SpellEffectInfo[] effects) => Spell(Bolt,
        effects.Length == 0 ? [Effect(SpellEffectName.SchoolDamage, 15, SpellImplicitTarget.UnitEnemy)] : effects) with
    {
        School = SpellSchool.Fire, DamageClass = SpellDamageClass.Magic,
        RangeIndex = 4, Range = new SpellRange(0, 30), StartRecoveryCategory = 0, StartRecoveryTime = 0,
    };

    private static (Player Attacker, FakeSession Session, Player Victim, Player Magnet) AddPlayers(SpellTestKit kit)
    {
        (Player attacker, FakeSession session) = kit.AddPlayer(1);
        Player victim = kit.AddPlayer(2, 5).Player;
        Player magnet = kit.AddPlayer(3, 6).Player;
        var relations = new FakeRelations();
        relations.Hostile.Add(attacker.Guid);
        kit.System.Relations = relations;
        kit.System.CombatRules = SpellCombatRules.Neutral;
        attacker.MaxHealth = attacker.Health = victim.MaxHealth = victim.Health = magnet.MaxHealth = magnet.Health = 100;
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(magnet, Protection, SpellCastTargets.ForUnit(victim.Guid), true));
        session.Clear();
        return (attacker, session, victim, magnet);
    }

    [Fact]
    public void MultipleEffectsRedirectTogether_ConsumeOnce_AndReportMagnetInSpellGo()
    {
        using var kit = new SpellTestKit(Protect(), Attack(
            Effect(SpellEffectName.SchoolDamage, 15, SpellImplicitTarget.UnitEnemy),
            Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.UnitEnemy)));
        var (attacker, session, victim, magnet) = AddPlayers(kit);
        Assert.True(kit.System.HasAuraHandler(AuraType.SpellMagnet));
        SpellCastTargets targets = SpellCastTargets.ForUnit(victim.Guid);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(attacker, Bolt, targets, true));
        Assert.Equal(100u, victim.Health);
        Assert.Equal(75u, magnet.Health);
        Assert.False(kit.System.HasAura(victim, Protection));
        var reader = new PacketReader(Assert.Single(Packets(session, WorldOpcode.SmsgSpellGo)));
        _ = reader.ReadPackedGuid();
        _ = reader.ReadPackedGuid();
        Assert.Equal(Bolt, reader.ReadUInt32());
        _ = reader.ReadUInt16();
        Assert.Equal(1, reader.ReadByte());
        Assert.Equal(magnet.Guid.Value, reader.ReadUInt64());
        Assert.Equal(0, reader.ReadByte());
        Assert.Equal(magnet.Guid, SpellCastTargets.Read(ref reader).Unit);

        Assert.Equal(victim.Guid, targets.Unit); // cast selection owns its copy of the request
        kit.System.CastSpell(attacker, Bolt, targets, true);
        Assert.Equal(75u, victim.Health); // protection was consumed, no dangling redirection
    }

    [Theory]
    [InlineData(SpellImplicitTarget.Unit)]
    [InlineData(SpellImplicitTarget.None)]
    public void LaterMixedSelectorsUseRedirectedExplicitTarget(SpellImplicitTarget selector)
    {
        using var kit = new SpellTestKit(Protect(), Attack(
            Effect(SpellEffectName.SchoolDamage, 15, SpellImplicitTarget.UnitEnemy),
            Effect(SpellEffectName.SchoolDamage, 10, selector)));
        var (attacker, _, victim, magnet) = AddPlayers(kit);
        kit.System.CastSpell(attacker, Bolt, SpellCastTargets.ForUnit(victim.Guid), true);
        Assert.Equal(100u, victim.Health);
        Assert.Equal(75u, magnet.Health);
    }

    [Fact]
    public void ConsumingPartyChildRemovesCasterSourceAndAllOtherPartyChildren()
    {
        using var kit = new SpellTestKit(Protect() with
        {
            Effects = [Effect(SpellEffectName.ApplyAreaAuraParty, 1, SpellImplicitTarget.UnitCaster, AuraType.SpellMagnet) with { Radius = 30 }],
        }, Attack());
        var (attacker, _, victim, magnet) = AddPlayers(kit);
        Player peer = kit.AddPlayer(4, 8).Player;
        var groups = new FakeGroups();
        groups.Parties.Add([victim.Guid, magnet.Guid, peer.Guid]);
        kit.System.Groups = groups;
        kit.System.CastSpell(magnet, Protection, SpellCastTargets.ForSelf(), true);
        kit.System.Update(0);
        Assert.True(kit.System.HasAura(peer, Protection));
        Assert.NotNull(Assert.Single(kit.System.GetAuras(victim)).AreaParent);
        kit.System.CastSpell(attacker, Bolt, SpellCastTargets.ForUnit(victim.Guid), true);
        Assert.False(kit.System.HasAura(magnet, Protection));
        Assert.False(kit.System.HasAura(victim, Protection));
        Assert.False(kit.System.HasAura(peer, Protection));
        kit.System.Update(0);
        Assert.False(kit.System.HasAura(victim, Protection));
    }

    [Theory]
    [InlineData(0)] // melee
    [InlineData(1)] // ranged
    [InlineData(2)] // no-redirection
    [InlineData(3)] // suppress target procs
    [InlineData(4)] // poison dispel
    [InlineData(5)] // ability
    [InlineData(6)] // area targeting
    [InlineData(7)] // friendly targeting
    public void ExcludedSpellsDoNotConsumeProtection(int exclusion)
    {
        SpellInfo attack = exclusion switch
        {
            0 => Attack() with { DamageClass = SpellDamageClass.Melee },
            1 => Attack() with { DamageClass = SpellDamageClass.Ranged },
            2 => Attack() with { AttributesEx = (SpellAttributesEx)8 },
            3 => Attack() with { AttributesEx3 = 0x20000 },
            4 => Attack() with { Dispel = 4 },
            5 => Attack() with { Attributes = SpellAttributes.IsAbility },
            6 => Attack(Effect(SpellEffectName.SchoolDamage, 15, SpellImplicitTarget.EnumUnitsEnemyAoeAtSrcLoc) with { Radius = 20 }),
            _ => Attack(Effect(SpellEffectName.Heal, 15, SpellImplicitTarget.UnitFriend)),
        };
        using var kit = new SpellTestKit(Protect(), attack);
        var (attacker, _, victim, _) = AddPlayers(kit);
        Unit caster = exclusion == 7 ? victim : attacker;
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, Bolt, SpellCastTargets.ForUnit(victim.Guid), true));
        Assert.Equal(1, Assert.Single(kit.System.GetAuras(victim)).Charges);
    }

    [Fact]
    public void RedirectedChainStopsAtMagnet_AndAMissStillConsumesCharge()
    {
        using var kit = new SpellTestKit(Protect(), Attack(Effect(SpellEffectName.SchoolDamage, 15, SpellImplicitTarget.UnitEnemy) with { ChainTarget = 3 }));
        var (attacker, session, victim, magnet) = AddPlayers(kit);
        kit.System.CombatRules = new FixedRules { Miss = SpellMissInfo.Resist };
        kit.System.CastSpell(attacker, Bolt, SpellCastTargets.ForUnit(victim.Guid), true);
        Assert.Equal(100u, victim.Health);
        Assert.Equal(100u, magnet.Health);
        Assert.False(kit.System.HasAura(victim, Protection));
        var reader = new PacketReader(Assert.Single(Packets(session, WorldOpcode.SmsgSpellGo)));
        _ = reader.ReadPackedGuid();
        _ = reader.ReadPackedGuid();
        _ = reader.ReadUInt32();
        _ = reader.ReadUInt16();
        Assert.Equal(0, reader.ReadByte());
        Assert.Equal(1, reader.ReadByte());
        Assert.Equal(magnet.Guid.Value, reader.ReadUInt64());
        Assert.Equal((byte)SpellMissInfo.Resist, reader.ReadByte());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void ChargesAreConsumedOncePerCast_ZeroMeansUnlimited(uint charges)
    {
        using var kit = new SpellTestKit(Protect(charges), Attack(
            Effect(SpellEffectName.SchoolDamage, 5, SpellImplicitTarget.UnitEnemy),
            Effect(SpellEffectName.SchoolDamage, 5, SpellImplicitTarget.UnitEnemy)));
        var (attacker, _, victim, magnet) = AddPlayers(kit);
        kit.System.CastSpell(attacker, Bolt, SpellCastTargets.ForUnit(victim.Guid), true);
        Assert.Equal(100u, victim.Health);
        Assert.Equal(90u, magnet.Health);
        Assert.Equal(charges == 0 ? 0 : 1, Assert.Single(kit.System.GetAuras(victim)).Charges);
        kit.System.CastSpell(attacker, Bolt, SpellCastTargets.ForUnit(victim.Guid), true);
        Assert.Equal(100u, victim.Health);
        Assert.Equal(charges == 0, kit.System.HasAura(victim, Protection));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeadOrRevokedCasterCannotRedirect(bool revoked)
    {
        using var kit = new SpellTestKit(Protect(), Attack());
        var (attacker, _, victim, magnet) = AddPlayers(kit);
        if (revoked)
        {
            kit.System.RemoveUnit(magnet);
        }
        else
        {
            magnet.Health = 0;
        }

        kit.System.CastSpell(attacker, Bolt, SpellCastTargets.ForUnit(victim.Guid), true);
        Assert.Equal(85u, victim.Health);
        Assert.Equal(1, Assert.Single(kit.System.GetAuras(victim)).Charges);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void RestrictedMagnetFallsBackWithoutConsumingProtection(int restriction)
    {
        using var kit = new SpellTestKit(Protect(), Attack());
        var (attacker, _, victim, magnet) = AddPlayers(kit);
        if (restriction == 0)
        {
            magnet.SetGameMaster(true);
        }
        else
        {
            magnet.UnitFlags |= restriction == 1 ? UnitFlags.NotSelectable : UnitFlags.Spawning;
        }

        kit.System.CastSpell(attacker, Bolt, SpellCastTargets.ForUnit(victim.Guid), true);
        Assert.Equal(85u, victim.Health);
        Assert.Equal(100u, magnet.Health);
        Assert.Equal(1, Assert.Single(kit.System.GetAuras(victim)).Charges);
    }

    [Fact]
    public void SelectionHappensAtCompletion_SurvivesProtectionRemovalDuringCastBar()
    {
        using var kit = new SpellTestKit(Protect(), Attack() with { CastTime = new SpellCastTime(1000, 0, 0) });
        var (attacker, _, victim, magnet) = AddPlayers(kit);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(attacker, Bolt, SpellCastTargets.ForUnit(victim.Guid), false));
        Assert.Equal(1, Assert.Single(kit.System.GetAuras(victim)).Charges);
        kit.System.RemoveAuras(victim, Protection);
        kit.Advance(1000);
        kit.Advance(kit.System.SpellBatchWindowMs); // with spell batching on, the held go resolves at the next boundary (0: no-op)
        Assert.Equal(85u, victim.Health);
        Assert.Equal(100u, magnet.Health);
    }

    [Fact]
    public void Visual7250Redirects_AndMagnetDoesNotAddRangeOrLosRestriction()
    {
        using var kit = new SpellTestKit(Protect(), Attack() with { DamageClass = SpellDamageClass.None, SpellVisual = 7250 });
        var (attacker, _, victim, magnet) = AddPlayers(kit);
        magnet.Relocate(50, 0, 0, 0, 0);
        var los = new FakeLineOfSight { WallX = 20 };
        WorldCollision.Of(kit.World).Install(los);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(attacker, Bolt, SpellCastTargets.ForUnit(victim.Guid), false));
        kit.Advance(kit.System.SpellBatchWindowMs); // with spell batching on, the held go resolves at the next boundary (0: no-op)
        Assert.Equal(100u, victim.Health);
        Assert.Equal(85u, magnet.Health);
    }

    [Fact]
    public void OriginalVictimLineOfSightFailureDoesNotConsumeCharge()
    {
        using var kit = new SpellTestKit(Protect(), Attack());
        var (attacker, _, victim, _) = AddPlayers(kit);
        WorldCollision.Of(kit.World).Install(new FakeLineOfSight { WallX = 2 });
        Assert.Equal(SpellCastResult.LineOfSight, kit.System.CastSpell(attacker, Bolt, SpellCastTargets.ForUnit(victim.Guid), false));
        Assert.Equal(1, Assert.Single(kit.System.GetAuras(victim)).Charges);
    }

    [Fact]
    public void ArcaneMissilesCasterSelectorPlacesChannelAuraOnMagnet()
    {
        using var kit = new SpellTestKit(Protect(), Attack(Effect(SpellEffectName.ApplyAura, 1, SpellImplicitTarget.UnitCaster, AuraType.Dummy)) with
        {
            SpellFamilyName = 3, SpellFamilyFlags = 0x800, Duration = new SpellDuration(3000, 0, 3000),
            AttributesEx = SpellAttributesEx.IsChanneled,
        });
        var (attacker, _, victim, magnet) = AddPlayers(kit);
        kit.System.CastSpell(attacker, Bolt, SpellCastTargets.ForUnit(victim.Guid), false);
        Assert.False(kit.System.HasAura(victim, Bolt));
        Assert.False(kit.System.HasAura(attacker, Bolt));
        Assert.True(kit.System.HasAura(magnet, Bolt));
        Assert.False(kit.System.HasAura(victim, Protection));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RedirectedChannelStopsItsPeriodicAura_WhenCancelledOrMagnetDies(bool dies)
    {
        const uint channel = 970010;
        using var kit = new SpellTestKit(Protect(), Attack(), Attack(
            Effect(SpellEffectName.ApplyAura, 1, SpellImplicitTarget.UnitCaster, AuraType.PeriodicTriggerSpell, amplitude: 1000, trigger: Bolt)) with
        {
            Id = channel, SpellFamilyName = 3, SpellFamilyFlags = 0x800,
            Duration = new SpellDuration(3000, 0, 3000), AttributesEx = SpellAttributesEx.IsChanneled,
        });
        var (attacker, _, victim, magnet) = AddPlayers(kit);
        magnet.Health = 10;
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(attacker, channel, SpellCastTargets.ForUnit(victim.Guid), false));
        Assert.True(kit.System.HasAura(magnet, channel));
        if (dies)
        {
            kit.Advance(1100);
            Assert.False(magnet.IsAlive);
        }
        else
        {
            kit.System.CancelChannel(attacker);
        }

        Assert.Null(kit.System.GetState(attacker.Guid)?.CurrentCast);
        Assert.False(kit.System.HasAura(magnet, channel));
        Assert.Equal(0u, attacker.GetUInt32(UpdateFields.UnitChannelSpell));
        kit.Advance(3000);
        Assert.Equal(100u, victim.Health);
    }

    private static SpellCastTargets ClientNamed(ObjectGuid unit)
    {
        var wire = new PacketWriter();
        wire.WriteUInt16((ushort)SpellCastTargetFlags.Unit);
        wire.WritePackedGuid(unit.Value);
        var reader = new PacketReader(wire.ToArray());
        return SpellCastTargets.Read(ref reader);
    }

    private sealed class TargetsAtFinish : ISpellCastObserver
    {
        public SpellCastTargets? Seen { get; private set; }

        public void OnFinished(SpellCast cast, bool completed) => Seen = cast.Targets;
    }

    [Fact]
    public void ClientNamedCast_IsNotRedirectedOntoAMagnetTheCasterMayNotAttack()
    {
        using var kit = new SpellTestKit(Protect(), Attack());
        var (attacker, _, victim, magnet) = AddPlayers(kit);
        ((FakeRelations)kit.System.Relations).Hostile.Add(magnet.Guid); // the magnet reads as a friend of the attacker, the victim as a foe

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(attacker, Bolt, ClientNamed(victim.Guid), true));

        Assert.Equal(100u, magnet.Health);
        Assert.Equal(85u, victim.Health);
        Assert.True(kit.System.HasAura(victim, Protection)); // no charge was spent on the refused redirect
    }

    [Fact]
    public void ClientNamedCast_RedirectedOntoAnAttackableMagnet_KeepsFromClient()
    {
        using var kit = new SpellTestKit(Protect(), Attack());
        var (attacker, _, victim, magnet) = AddPlayers(kit);
        var seen = new TargetsAtFinish();
        kit.System.RegisterObserver(seen);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(attacker, Bolt, ClientNamed(victim.Guid), true));

        Assert.Equal(85u, magnet.Health);
        Assert.NotNull(seen.Seen);
        Assert.Equal(magnet.Guid, seen.Seen!.Unit);
        Assert.True(seen.Seen.FromClient); // the redirected block stays client-named (SpellCastTargets.ForUnit(guid, parent) inherits the flag, see SpellCastTargets.cs)
    }

    /// <summary>The world daemon's resolver (WorldSpellUnitResolver): players and creatures through the map's object registry.</summary>
    private sealed class MapObjectResolver : ISpellUnitResolver
    {
        public Unit? Find(Unit reference, ObjectGuid guid)
            => guid.IsEmpty ? null : reference.Guid == guid ? reference : reference.Map?.FindObject(guid) as Unit;
    }

    /// <summary>
    /// FactionTemplate.dbc (5875) row 1, the Human player template: faction 1, flags 72, own mask 3, friendly mask 2, hostile mask 12. Unlike
    /// ReputationFixtures.PlayerTemplate (own 1, friendly 2) it is friendly to itself, so a totem that copied its owner's template reads as a friend
    /// of every player on that template, as it does on real data.
    /// </summary>
    private static readonly FactionTemplateCatalog SelfFriendlyPlayerTemplate = new([new FactionTemplateRecord(1, 1, 72, 3, 2, 12)]);

    /// <summary>
    /// The production hook chain (ReputationCombatHooks over FactionCombatHooks) with a creature that a player owns and that carries the
    /// player-controlled flag, as a summoned totem does (TotemSystem), on the real self-friendly player template. The attacker and the victim are
    /// same-team free-for-all players, and in every case the template reaction refuses the totem (the preconditions below), so only
    /// IsFreeForAllOpponentOwned can let the redirect through. Grounding between free-for-all opponents must still redirect (mangos-classic
    /// GetReactionTo, vmangos GetReactionTo: a pair of free-for-all controlling players is hostile); an owner who is not free-for-all, or who is
    /// grouped with the caster, keeps the totem unattackable.
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void ClientCast_TeamTotem_IsRedirectedOnlyWhenItsOwnerIsAnUngroupedFreeForAllOpponent(bool ownerFreeForAll, bool ownerGroupedWithCaster)
    {
        using var kit = new SpellTestKit(Protect(), Attack());
        Map map = kit.World.GetMap(0);
        var creatures = new CreatureMapSystem(map, CreatureTestKit.Content([CreatureTestKit.Template(CreatureTestKit.WolfEntry)], []), random: new Random(1));
        map.AddUpdater(creatures);
        kit.System.Units = new MapObjectResolver();
        (Player attacker, _) = kit.AddPlayer(1);
        Player victim = kit.AddPlayer(2, 5).Player;
        Player owner = kit.AddPlayer(3, 6).Player;
        var reputation = new ReputationService(ReputationFixtures.Factions, roll: () => 0.0);
        foreach (Player player in new[] { attacker, victim, owner })
        {
            reputation.Track(player, reputation.Create(player, CharacterReputationData.Empty));
            player.MaxHealth = player.Health = 100;
        }

        map.Combat.Hooks = new ReputationCombatHooks(new FactionCombatHooks(SelfFriendlyPlayerTemplate),
            new ReputationReactionResolver(SelfFriendlyPlayerTemplate, ReputationFixtures.Factions, reputation.For));
        kit.System.Relations = CombatHookRelations.Instance;
        kit.System.CombatRules = SpellCombatRules.Neutral;

        attacker.Flags |= PlayerFlags.FfaPvp;
        victim.Flags |= PlayerFlags.FfaPvp;
        if (ownerFreeForAll)
        {
            owner.Flags |= PlayerFlags.FfaPvp;
        }

        if (ownerGroupedWithCaster)
        {
            CombatHooks.RegisterSameGroup(kit.World, (a, b) => (ReferenceEquals(a, attacker) && ReferenceEquals(b, owner)) || (ReferenceEquals(a, owner) && ReferenceEquals(b, attacker)));
        }

        Creature totem = creatures.SpawnTemporary(CreatureTestKit.Template(CreatureTestKit.WolfEntry), 4, 0, 83.5f, 0);
        totem.SetOwnerGuid(owner.Guid);
        totem.UnitFlags |= UnitFlags.PlayerControlled;
        totem.FactionTemplate = owner.FactionTemplate;
        totem.MaxHealth = totem.Health = 100;
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(totem, Protection, SpellCastTargets.ForUnit(victim.Guid), true));
        Assert.True(kit.System.HasAura(victim, Protection));
        Assert.True(CombatHookRelations.Instance.IsHostile(attacker, victim));
        Assert.True(CombatHookRelations.Instance.IsFriendly(attacker, totem)); // the template reaction reads the team's totem as a friend...
        Assert.False(CombatHookRelations.Instance.IsHostile(attacker, totem)); // ...so the explicit-hostile gate refuses it: the gap the exemption closes

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(attacker, Bolt, ClientNamed(victim.Guid), false));
        kit.Advance(kit.System.SpellBatchWindowMs); // with spell batching on, the held go resolves at the next boundary (0: no-op)

        if (ownerFreeForAll && !ownerGroupedWithCaster)
        {
            Assert.Equal(100u, victim.Health);
            Assert.Equal(85u, totem.Health);
            Assert.False(kit.System.HasAura(victim, Protection)); // the charge was spent
        }
        else
        {
            Assert.Equal(85u, victim.Health);
            Assert.Equal(100u, totem.Health);
            Assert.True(kit.System.HasAura(victim, Protection)); // no charge was spent on the refused redirect
        }
    }
}
