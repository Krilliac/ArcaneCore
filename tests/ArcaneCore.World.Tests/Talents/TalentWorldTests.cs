using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Talents;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.Npc;
using ArcaneCore.World.Talents;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Talents.TalentWorldFixture;

namespace ArcaneCore.World.Tests.Talents;

/// <summary>
/// The talent feature end to end: real sessions over loopback, in-memory stores, a synthetic Talent.dbc catalog, and the
/// quest-interaction creature as a warrior class trainer. Handlers and gossip wiring per vmangos SkillHandler.cpp:32-58.
/// </summary>
public sealed class TalentWorldTests
{
    private const uint Gold = 10000;
    private static readonly ObjectGuid Trainer = QuestInteractionFixture.Guid;

    private static WorldTestHost Start(TalentWorldFixture? fixture = null)
    {
        TalentTestServices.Current.Value = fixture;
        QuestInteractionTestServices.Current.Value = fixture is null ? null : new QuestInteractionFixture();
        try
        {
            return WorldTestHost.Start();
        }
        finally
        {
            TalentTestServices.Current.Value = null;
            QuestInteractionTestServices.Current.Value = null;
        }
    }

    private static async Task<(WorldTestClient Client, CharacterRecord Record, byte[] Self)> EnterAsync(
        WorldTestHost host, string name, byte level = 10, uint money = 0, AccountSecurity security = AccountSecurity.Player)
    {
        byte[] key = await host.AddAccountAsync(name, security);
        WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync(name, key);
        await client.CreateCharacterAsync(name);
        Account account = (await host.Accounts.FindByUsernameAsync(name))!;
        CharacterRecord record = (await host.Characters.GetByAccountAsync(account.Id)).Single();
        record.Level = level;
        record.Money = money;
        byte[] self = await client.LoginAsync((ulong)record.Id);
        return (client, record, self);
    }

    private static async Task<byte[]> RelogAsync(WorldTestHost host, WorldTestClient client, CharacterRecord record)
    {
        await client.SendAsync(WorldOpcode.CmsgLogoutRequest, []);
        await client.ReadUntilAsync(WorldOpcode.SmsgLogoutComplete);
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer(record.Name) is null, "the character leaves the world");
        return await client.LoginAsync((ulong)record.Id);
    }

    private static async Task MakeTrainerAsync(WorldTestHost host, string name)
    {
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer(name)!.VisibleObjects.Contains(Trainer), "the trainer becomes visible");
        await host.OnWorldAsync(() => ((Creature)host.World.FindOnlinePlayer(name)!.Map!.FindObject(Trainer)!).NpcFlags
            = (uint)(NpcFlags.Gossip | NpcFlags.Trainer));
    }

    private static byte[] U32U32(uint a, uint b)
    {
        var writer = new PacketWriter(8);
        writer.WriteUInt32(a);
        writer.WriteUInt32(b);
        return writer.ToArray();
    }

    private static byte[] GuidBody(ObjectGuid guid)
    {
        var writer = new PacketWriter(8);
        writer.WriteUInt64(guid.Value);
        return writer.ToArray();
    }

    private static TalentFeature Talents(WorldTestHost host) => host.WorldServices.GetRequiredService<TalentFeature>();

    private static uint FreePoints(WorldTestHost host, string name)
        => host.World.FindOnlinePlayer(name)!.GetUInt32(UpdateFields.PlayerCharacterPoints1);

    private static bool Knows(WorldTestHost host, string name, uint spell)
        => host.WorldServices.GetRequiredService<SpellFeature>().Spellbook.HasSpell(host.World.FindOnlinePlayer(name)!, spell);

    /// <summary>The first object block of a self create (build 5875 layout, as the quest journal tests read it).</summary>
    private static Dictionary<int, uint> ReadSelfFields(byte[] body)
    {
        var reader = new PacketReader(body);
        Assert.True(reader.ReadUInt32() >= 1);
        reader.ReadByte();
        byte type = reader.ReadByte();
        Assert.True(type is 2 or 3);
        reader.ReadPackedGuid();
        Assert.Equal((byte)4, reader.ReadByte());
        var flags = (ObjectUpdateFlags)reader.ReadByte();
        Assert.True((flags & ObjectUpdateFlags.Living) != 0);
        MovementInfo.Read(ref reader);
        reader.Skip(6 * 4);
        if ((flags & ObjectUpdateFlags.HighGuid) != 0)
        {
            reader.Skip(4);
        }

        if ((flags & ObjectUpdateFlags.All) != 0)
        {
            reader.Skip(4);
        }

        if ((flags & ObjectUpdateFlags.MeleeAttacking) != 0)
        {
            reader.ReadPackedGuid();
        }

        if ((flags & ObjectUpdateFlags.Transport) != 0)
        {
            reader.Skip(4);
        }

        int count = reader.ReadByte();
        uint[] masks = new uint[count];
        for (int i = 0; i < count; i++)
        {
            masks[i] = reader.ReadUInt32();
        }

        var values = new Dictionary<int, uint>();
        for (int field = 0; field < count * 32; field++)
        {
            if ((masks[field >> 5] & (1u << (field & 31))) != 0)
            {
                values[field] = reader.ReadUInt32();
            }
        }

        return values;
    }

    private static Dictionary<int, uint> ReadValuesBlock(byte[] body)
    {
        var reader = new PacketReader(body);
        Assert.Equal(1u, reader.ReadUInt32());
        reader.ReadByte();
        Assert.Equal((byte)ObjectUpdateType.Values, reader.ReadByte());
        reader.ReadPackedGuid();
        int blocks = reader.ReadByte();
        uint[] mask = new uint[blocks];
        for (int i = 0; i < blocks; i++)
        {
            mask[i] = reader.ReadUInt32();
        }

        var fields = new Dictionary<int, uint>();
        for (int index = 0; index < blocks * 32; index++)
        {
            if ((mask[index >> 5] & (1u << (index & 31))) != 0)
            {
                fields[index] = reader.ReadUInt32();
            }
        }

        return fields;
    }

    // ---- registration and framing ----------------------------------------------------------

    [Fact]
    public void Handlers_RegisterLearnAndWipeConfirm_ButNotTheInvalidUnlearnTalents()
    {
        var table = new OpcodeTable();
        new TalentHandlers().Register(table);

        Assert.True(table.TryGet(WorldOpcode.CmsgLearnTalent, out _));
        Assert.True(table.TryGet(WorldOpcode.MsgTalentWipeConfirm, out _));
        Assert.False(table.TryGet(WorldOpcode.CmsgUnlearnTalents, out _));   // vmangos Opcodes.cpp:622 INVALID_PACKET
    }

    public static TheoryData<WorldOpcode, int> MalformedLengths() => new()
    {
        { WorldOpcode.CmsgLearnTalent, 7 },
        { WorldOpcode.CmsgLearnTalent, 9 },
        { WorldOpcode.MsgTalentWipeConfirm, 7 },
        { WorldOpcode.MsgTalentWipeConfirm, 9 },
    };

    [Theory]
    [MemberData(nameof(MalformedLengths))]
    public async Task MalformedPayload_Disconnects(WorldOpcode opcode, int length)
    {
        await using WorldTestHost host = Start();
        (WorldTestClient client, _, _) = await EnterAsync(host, "BADTALENT");
        await using WorldTestClient clientScope = client;
        await client.CollectAsync();

        await client.SendAsync(opcode, new byte[length]);

        Assert.True(await client.IsClosedByServerAsync());
    }

    // ---- inert without Talent.dbc ------------------------------------------------------------

    [Fact]
    public async Task WithoutDbcPaths_TheFeatureIsInert_NoPointsAndLearnIsIgnored()
    {
        await using WorldTestHost host = Start();
        (WorldTestClient client, _, byte[] self) = await EnterAsync(host, "INERT", level: 10);
        await using WorldTestClient clientScope = client;
        Assert.Null(Talents(host).Service);
        Assert.Equal(0u, ReadSelfFields(self).GetValueOrDefault(UpdateFields.PlayerCharacterPoints1));
        await client.CollectAsync();

        await client.SendAsync(WorldOpcode.CmsgLearnTalent, U32U32(1, 0));

        Assert.DoesNotContain((await client.CollectAsync()).Select(p => p.Opcode), op => op == WorldOpcode.SmsgLearnedSpell);
    }

    // ---- login points and learning ---------------------------------------------------------

    [Theory]
    [InlineData(10, 1u)]
    [InlineData(9, 0u)]
    [InlineData(20, 11u)]
    public async Task Login_PutsTheFreePointsInTheSelfCreate(byte level, uint expected)
    {
        await using WorldTestHost host = Start(new TalentWorldFixture());
        (WorldTestClient client, _, byte[] self) = await EnterAsync(host, "LOGINPTS", level);
        await using WorldTestClient clientScope = client;

        Assert.Equal(expected, ReadSelfFields(self).GetValueOrDefault(UpdateFields.PlayerCharacterPoints1));
    }

    [Fact]
    public async Task LearnTalent_AnswersLearnedSpellAndAValuesUpdate_AndSurvivesRelogging()
    {
        await using WorldTestHost host = Start(new TalentWorldFixture());
        (WorldTestClient client, CharacterRecord record, _) = await EnterAsync(host, "LEARNER", security: AccountSecurity.GameMaster);
        await using WorldTestClient clientScope = client;
        await client.CollectAsync();

        await client.SendAsync(WorldOpcode.CmsgLearnTalent, U32U32(1, 0));

        Assert.Equal(BitConverter.GetBytes(T1R1), await client.ReadUntilAsync(WorldOpcode.SmsgLearnedSpell));
        Dictionary<int, uint> values = ReadValuesBlock(await client.ReadUpdateAsync());
        Assert.Equal(0u, values[UpdateFields.PlayerCharacterPoints1]);
        Assert.True(await host.OnWorldAsync(() => Knows(host, "LEARNER", T1R1)));

        byte[] self = await RelogAsync(host, client, record);

        Assert.Equal(0u, ReadSelfFields(self).GetValueOrDefault(UpdateFields.PlayerCharacterPoints1));   // the book keeps the spent point (zero fields are not in a create)
        Assert.True(await host.OnWorldAsync(() => Knows(host, "LEARNER", T1R1)));
    }

    [Fact]
    public async Task LearnTalent_WithAnUnmetTierRequirement_SendsNothing()
    {
        await using WorldTestHost host = Start(new TalentWorldFixture());
        (WorldTestClient client, _, _) = await EnterAsync(host, "TIERLOCK", level: 12);
        await using WorldTestClient clientScope = client;
        await client.CollectAsync();

        await client.SendAsync(WorldOpcode.CmsgLearnTalent, U32U32(2, 0));   // row 1 needs five points in the tree

        Assert.Empty(await client.CollectAsync());
        Assert.Equal(3u, await host.OnWorldAsync(() => FreePoints(host, "TIERLOCK")));
    }

    [Fact]
    public async Task ALevelUp_RaisesTheFreePoints()
    {
        await using WorldTestHost host = Start(new TalentWorldFixture());
        (WorldTestClient client, _, _) = await EnterAsync(host, "LEVELER", level: 9);
        await using WorldTestClient clientScope = client;
        Assert.Equal(0u, await host.OnWorldAsync(() => FreePoints(host, "LEVELER")));

        await host.OnWorldAsync(() =>
        {
            // Real XP, so the points come from the LevelChanged subscription and not from a manual call.
            Player player = host.World.FindOnlinePlayer("LEVELER")!;
            host.WorldServices.GetRequiredService<ArcaneCore.World.Progression.ProgressionFeature>().Progression
                .GiveXp(player, player.GetUInt32(UpdateFields.PlayerNextLevelXp));
            Assert.Equal(10, player.Level);
        });

        Assert.Equal(1u, await host.OnWorldAsync(() => FreePoints(host, "LEVELER")));
    }

    // ---- the wipe flow ---------------------------------------------------------------------

    [Fact]
    public async Task Wipe_RemovesTheTalent_ChargesGold_AndTheTrainerCastsTheVisual()
    {
        await using WorldTestHost host = Start(new TalentWorldFixture());
        (WorldTestClient client, _, _) = await EnterAsync(host, "WIPER", money: 100 * Gold);
        await using WorldTestClient clientScope = client;
        await MakeTrainerAsync(host, "WIPER");
        await client.SendAsync(WorldOpcode.CmsgLearnTalent, U32U32(1, 0));
        await client.ReadUntilAsync(WorldOpcode.SmsgLearnedSpell);
        await client.CollectAsync();

        await client.SendAsync(WorldOpcode.MsgTalentWipeConfirm, GuidBody(Trainer));

        List<(WorldOpcode Opcode, byte[] Payload)> packets = await client.CollectAsync();
        Assert.Equal(BitConverter.GetBytes((ushort)T1R1), Assert.Single(packets, p => p.Opcode == WorldOpcode.SmsgRemovedSpell).Payload);
        Assert.Contains(packets, p => p.Opcode == WorldOpcode.SmsgSpellGo && CastBy(p.Payload, Trainer, UntalentVisual));
        Assert.Equal(99 * Gold, await host.OnWorldAsync(() => host.World.FindOnlinePlayer("WIPER")!.Money));
        Assert.Equal(1u, await host.OnWorldAsync(() => FreePoints(host, "WIPER")));
        Assert.False(await host.OnWorldAsync(() => Knows(host, "WIPER", T1R1)));
        Assert.Equal(1u, await host.OnWorldAsync(() => Talents(host).Service!.StateOf(host.World.FindOnlinePlayer("WIPER")!).Respec.Multiplier));

        // The next wipe is priced at 5 gold (1 -> 5 -> 10 ...).
        await client.SendAsync(WorldOpcode.CmsgLearnTalent, U32U32(1, 0));
        await client.ReadUntilAsync(WorldOpcode.SmsgLearnedSpell);
        await client.SendAsync(WorldOpcode.MsgTalentWipeConfirm, GuidBody(Trainer));
        await client.ReadUntilAsync(WorldOpcode.SmsgRemovedSpell);
        Assert.Equal(94 * Gold, await host.OnWorldAsync(() => host.World.FindOnlinePlayer("WIPER")!.Money));
    }

    private static bool CastBy(byte[] spellGo, ObjectGuid caster, uint spell)
    {
        var reader = new PacketReader(spellGo);
        reader.ReadPackedGuid();                       // cast item or caster
        ulong casterGuid = reader.ReadPackedGuid();
        return casterGuid == caster.Value && reader.ReadUInt32() == spell;
    }

    [Fact]
    public async Task Wipe_WithNothingSpent_AnswersTheEmptyConfirm()
    {
        await using WorldTestHost host = Start(new TalentWorldFixture());
        (WorldTestClient client, _, _) = await EnterAsync(host, "NOSPEND", money: 5 * Gold);
        await using WorldTestClient clientScope = client;
        await MakeTrainerAsync(host, "NOSPEND");
        await client.CollectAsync();

        await client.SendAsync(WorldOpcode.MsgTalentWipeConfirm, GuidBody(Trainer));

        byte[] empty = await client.ReadUntilAsync(WorldOpcode.MsgTalentWipeConfirm);
        Assert.Equal(TalentPackets.WipeConfirm(ObjectGuid.Empty, Gold).ToArray(), empty);   // vmangos fills the cost field (Player.cpp:8259-8265)
        Assert.Equal(5 * Gold, await host.OnWorldAsync(() => host.World.FindOnlinePlayer("NOSPEND")!.Money));
    }

    [Fact]
    public async Task Wipe_WithTooLittleMoney_SendsBuyFailedThenTheEmptyConfirm()
    {
        await using WorldTestHost host = Start(new TalentWorldFixture());
        (WorldTestClient client, _, _) = await EnterAsync(host, "BROKE", money: Gold - 1);
        await using WorldTestClient clientScope = client;
        await MakeTrainerAsync(host, "BROKE");
        await client.SendAsync(WorldOpcode.CmsgLearnTalent, U32U32(1, 0));
        await client.ReadUntilAsync(WorldOpcode.SmsgLearnedSpell);
        await client.CollectAsync();

        await client.SendAsync(WorldOpcode.MsgTalentWipeConfirm, GuidBody(Trainer));

        List<WorldOpcode> order = [.. (await client.CollectAsync()).Select(p => p.Opcode)
            .Where(o => o is WorldOpcode.SmsgBuyFailed or WorldOpcode.MsgTalentWipeConfirm or WorldOpcode.SmsgRemovedSpell)];
        Assert.Equal([WorldOpcode.SmsgBuyFailed, WorldOpcode.MsgTalentWipeConfirm], order);
        Assert.True(await host.OnWorldAsync(() => Knows(host, "BROKE", T1R1)));
    }

    [Fact]
    public async Task Wipe_WithTooLittleMoney_CanSkipTheEmptyConfirm()
    {
        var fixture = new TalentWorldFixture();
        fixture.Settings["Talents:WipeRefusalAlsoSendsEmptyConfirm"] = "false";
        await using WorldTestHost host = Start(fixture);
        (WorldTestClient client, _, _) = await EnterAsync(host, "BROKEAGAIN", money: Gold - 1);
        await using WorldTestClient clientScope = client;
        await MakeTrainerAsync(host, "BROKEAGAIN");
        await client.SendAsync(WorldOpcode.CmsgLearnTalent, U32U32(1, 0));
        await client.ReadUntilAsync(WorldOpcode.SmsgLearnedSpell);
        await client.CollectAsync();

        await client.SendAsync(WorldOpcode.MsgTalentWipeConfirm, GuidBody(Trainer));

        List<WorldOpcode> opcodes = [.. (await client.CollectAsync()).Select(p => p.Opcode)];
        Assert.Contains(WorldOpcode.SmsgBuyFailed, opcodes);
        Assert.DoesNotContain(WorldOpcode.MsgTalentWipeConfirm, opcodes);
    }

    [Theory]
    [InlineData(true, false)]    // default: a trainer of another class is refused
    [InlineData(false, true)]    // RequireClassTrainerForWipe=false: vmangos behaviour, any reachable trainer
    public async Task Wipe_AtATrainerOfAnotherClass_FollowsTheSwitch(bool requireClass, bool expectReset)
    {
        var fixture = new TalentWorldFixture { TrainerClass = 2 };
        fixture.Settings["Talents:RequireClassTrainerForWipe"] = requireClass ? "true" : "false";
        await using WorldTestHost host = Start(fixture);
        (WorldTestClient client, _, _) = await EnterAsync(host, "WRONGCLASS", money: 10 * Gold);
        await using WorldTestClient clientScope = client;
        await MakeTrainerAsync(host, "WRONGCLASS");
        await client.SendAsync(WorldOpcode.CmsgLearnTalent, U32U32(1, 0));
        await client.ReadUntilAsync(WorldOpcode.SmsgLearnedSpell);
        await client.CollectAsync();

        await client.SendAsync(WorldOpcode.MsgTalentWipeConfirm, GuidBody(Trainer));

        List<WorldOpcode> opcodes = [.. (await client.CollectAsync()).Select(p => p.Opcode)];
        Assert.Equal(expectReset, opcodes.Contains(WorldOpcode.SmsgRemovedSpell));
        Assert.Equal(!expectReset, await host.OnWorldAsync(() => Knows(host, "WRONGCLASS", T1R1)));
    }

    [Fact]
    public async Task Wipe_AtSomethingThatIsNotATrainer_IsIgnored()
    {
        await using WorldTestHost host = Start(new TalentWorldFixture());
        (WorldTestClient client, _, _) = await EnterAsync(host, "NOTRAINER", money: 10 * Gold);
        await using WorldTestClient clientScope = client;
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("NOTRAINER")!.VisibleObjects.Contains(Trainer), "the creature becomes visible");
        await client.SendAsync(WorldOpcode.CmsgLearnTalent, U32U32(1, 0));
        await client.ReadUntilAsync(WorldOpcode.SmsgLearnedSpell);
        await client.CollectAsync();

        await client.SendAsync(WorldOpcode.MsgTalentWipeConfirm, GuidBody(Trainer));   // the creature is only a quest giver

        Assert.Empty(await client.CollectAsync());
        Assert.True(await host.OnWorldAsync(() => Knows(host, "NOTRAINER", T1R1)));
    }

    [Fact]
    public async Task Gossip_OffersTheUnlearnOptionToALevelTenClass_AndSelectingItAsksForConfirmation()
    {
        await using WorldTestHost host = Start(new TalentWorldFixture());
        (WorldTestClient client, _, _) = await EnterAsync(host, "GOSSIPER", money: 100 * Gold);
        await using WorldTestClient clientScope = client;
        await MakeTrainerAsync(host, "GOSSIPER");
        await client.CollectAsync();

        await client.SendAsync(WorldOpcode.CmsgGossipHello, GuidBody(Trainer));
        var menu = new PacketReader(await client.ReadUntilAsync(WorldOpcode.SmsgGossipMessage));
        menu.ReadUInt64();
        menu.ReadUInt32();
        Assert.Equal(1u, menu.ReadUInt32());

        await client.SendAsync(WorldOpcode.CmsgGossipSelectOption, [.. GuidBody(Trainer), .. BitConverter.GetBytes(0u)]);

        List<(WorldOpcode Opcode, byte[] Payload)> packets = await client.CollectAsync();
        Assert.Contains(packets, p => p.Opcode == WorldOpcode.SmsgGossipComplete);
        Assert.Equal(TalentPackets.WipeConfirm(Trainer, Gold).ToArray(), Assert.Single(packets, p => p.Opcode == WorldOpcode.MsgTalentWipeConfirm).Payload);

        // Below level 10 the option is not offered (the positive control above shows it is wired).
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("GOSSIPER")!.Level = 9);
        await client.SendAsync(WorldOpcode.CmsgGossipHello, GuidBody(Trainer));
        menu = new PacketReader(await client.ReadUntilAsync(WorldOpcode.SmsgGossipMessage));
        menu.ReadUInt64();
        menu.ReadUInt32();
        Assert.Equal(0u, menu.ReadUInt32());
    }

    // ---- persistence -----------------------------------------------------------------------

    [Fact]
    public async Task ARespec_IsPersisted_AndRestoredAtLogin()
    {
        var fixture = new TalentWorldFixture();
        await using WorldTestHost host = Start(fixture);
        (WorldTestClient client, CharacterRecord record, _) = await EnterAsync(host, "SAVER", money: 100 * Gold, security: AccountSecurity.GameMaster);
        await using WorldTestClient clientScope = client;
        await MakeTrainerAsync(host, "SAVER");
        await client.SendAsync(WorldOpcode.CmsgLearnTalent, U32U32(1, 0));
        await client.ReadUntilAsync(WorldOpcode.SmsgLearnedSpell);
        await client.SendAsync(WorldOpcode.MsgTalentWipeConfirm, GuidBody(Trainer));
        await client.ReadUntilAsync(WorldOpcode.SmsgRemovedSpell);
        await Talents(host).Persistence.FlushAsync();

        CharacterTalentSnapshot stored = Snapshot(fixture, record.Id);
        Assert.Equal(1u, stored.Multiplier);
        Assert.True(stored.TimeUnix > 1_700_000_000);

        await RelogAsync(host, client, record);

        Assert.Equal(
            new RespecState(stored.Multiplier, stored.TimeUnix),
            await host.OnWorldAsync(() => Talents(host).Service!.StateOf(host.World.FindOnlinePlayer("SAVER")!).Respec));
    }

    private readonly record struct CharacterTalentSnapshot(uint Multiplier, long TimeUnix);

    private static CharacterTalentSnapshot Snapshot(TalentWorldFixture fixture, int characterId)
        => fixture.Store.RespecOf(characterId) is { } state ? new(state.ResetMultiplier, state.ResetTimeUnix) : throw new Xunit.Sdk.XunitException("no respec row");

    [Fact]
    public async Task DisabledRanks_RoundTripThroughTheStore_AndComeBackWithTheirTalent()
    {
        var fixture = new TalentWorldFixture();
        await using WorldTestHost host = Start(fixture);
        (WorldTestClient client, CharacterRecord record, _) = await EnterAsync(host, "DISABLER", money: 100 * Gold, security: AccountSecurity.GameMaster);
        await using WorldTestClient clientScope = client;
        await MakeTrainerAsync(host, "DISABLER");
        await client.SendAsync(WorldOpcode.CmsgLearnTalent, U32U32(5, 0));            // the ability talent P1
        await client.ReadUntilAsync(WorldOpcode.SmsgLearnedSpell);
        await host.OnWorldAsync(() => host.WorldServices.GetRequiredService<SpellFeature>().System
            .LearnSpell(host.World.FindOnlinePlayer("DISABLER")!, P2));                // the trainer-learned rank 2
        await client.CollectAsync();

        await client.SendAsync(WorldOpcode.MsgTalentWipeConfirm, GuidBody(Trainer));
        await client.CollectAsync();
        await Talents(host).Persistence.FlushAsync();

        Assert.False(await host.OnWorldAsync(() => Knows(host, "DISABLER", P1)));
        Assert.False(await host.OnWorldAsync(() => Knows(host, "DISABLER", P2)));      // disabled, not kept
        Assert.Equal([P2], fixture.Store.DisabledOf(record.Id));

        await RelogAsync(host, client, record);
        Assert.False(await host.OnWorldAsync(() => Knows(host, "DISABLER", P2)));      // still hidden after a relog
        await client.CollectAsync();

        await client.SendAsync(WorldOpcode.CmsgLearnTalent, U32U32(5, 0));
        List<uint> learned = [];
        learned.Add(BitConverter.ToUInt32(await client.ReadUntilAsync(WorldOpcode.SmsgLearnedSpell)));
        learned.Add(BitConverter.ToUInt32(await client.ReadUntilAsync(WorldOpcode.SmsgLearnedSpell)));
        Assert.Equal([P1, P2], learned);
        await Talents(host).Persistence.FlushAsync();
        Assert.Empty(fixture.Store.DisabledOf(record.Id));
    }

    [Fact]
    public async Task ASpellInBothStores_LoadsAsDisabled()
    {
        // The crash window between the two independent queues can leave a spell row beside its disabled row; the safe reading
        // loses an ability, it never grants one.
        var fixture = new TalentWorldFixture();
        await using WorldTestHost host = Start(fixture);
        byte[] key = await host.AddAccountAsync("CRASHED");
        WorldTestClient client = await host.ConnectAsync();
        await using WorldTestClient clientScope = client;
        await client.AuthenticateAsync("CRASHED", key);
        await client.CreateCharacterAsync("Crashed");
        Account account = (await host.Accounts.FindByUsernameAsync("CRASHED"))!;
        CharacterRecord record = (await host.Characters.GetByAccountAsync(account.Id)).Single();
        await host.WorldServices.GetRequiredService<ArcaneCore.Data.Characters.Spells.ICharacterSpellStore>().AddAsync(record.Id, [P2]);
        fixture.Store.SeedDisabled(record.Id, P2);

        await client.LoginAsync((ulong)record.Id);

        Assert.False(await host.OnWorldAsync(() => Knows(host, "Crashed", P2)));
    }

    [Fact]
    public async Task AFailedWrite_IsRetained_AndReconciledAtTheNextLogin()
    {
        var fixture = new TalentWorldFixture();
        await using WorldTestHost host = Start(fixture);
        (WorldTestClient client, CharacterRecord record, _) = await EnterAsync(host, "FLAKY", money: 100 * Gold, security: AccountSecurity.GameMaster);
        await using WorldTestClient clientScope = client;
        await MakeTrainerAsync(host, "FLAKY");
        await client.SendAsync(WorldOpcode.CmsgLearnTalent, U32U32(1, 0));
        await client.ReadUntilAsync(WorldOpcode.SmsgLearnedSpell);
        fixture.Store.FailWrites = true;

        await client.SendAsync(WorldOpcode.MsgTalentWipeConfirm, GuidBody(Trainer));
        await client.ReadUntilAsync(WorldOpcode.SmsgRemovedSpell);
        await Talents(host).Persistence.FlushAsync();

        Assert.Contains(record.Id, Talents(host).Persistence.FailedCharacters);
        Assert.Null(fixture.Store.RespecOf(record.Id));

        fixture.Store.FailWrites = false;
        await RelogAsync(host, client, record);

        Assert.Equal(1u, fixture.Store.RespecOf(record.Id)!.ResetMultiplier);          // the retained change reached the store
        Assert.Empty(Talents(host).Persistence.FailedCharacters);
        Assert.Equal(
            1u,
            await host.OnWorldAsync(() => Talents(host).Service!.StateOf(host.World.FindOnlinePlayer("FLAKY")!).Respec.Multiplier));
    }
}
