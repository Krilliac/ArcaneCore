using System.Buffers.Binary;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using ArcaneCore.World.Characters.Creation;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Characters.Creation;

/// <summary>
/// The starting action bar (vmangos MasterPlayer::Create, MasterPlayer.cpp:30-39): a new character's
/// first SMSG_ACTION_BUTTONS carries the <c>playercreateinfo_action</c> rows of its race and class.
/// </summary>
public sealed class StartActionsWorldTests
{
    /// <summary>Human warrior rows: two spells on 72 and 73, an item on 83 and one row out of range.</summary>
    private sealed class FakeSource : IStartActionSource
    {
        // Spells that exist in the host spell store: the creation hook drops rows naming unknown spells.
        public uint SpellA { get; set; }

        public uint SpellB { get; set; }

        public Task<IReadOnlyList<ActionButton>> GetAsync(byte race, byte cls, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ActionButton>>(race == 1 && cls == 1
                ? [new ActionButton(72, SpellA, 0), new ActionButton(73, SpellB, 0), new ActionButton(83, 117, 128), new ActionButton(130, 5, 0)]
                : []);
    }

    private static (WorldTestHost Host, FakeSource Source) Start(params (string Key, string Value)[] settings)
    {
        var source = new FakeSource();
        Action<IServiceCollection> config = CreateHandlerOrderTests.Config(settings);
        WorldTestHost host = WorldTestHost.Start(configureServices: services =>
        {
            config(services);
            services.AddSingleton<IStartActionSource>(source);
        });
        uint[] spells = [.. host.WorldServices.GetRequiredService<SpellFeature>().System.Store.All.Select(s => s.Id).Take(2)];
        Assert.Equal(2, spells.Length);
        (source.SpellA, source.SpellB) = (spells[0], spells[1]);
        return (host, source);
    }

    private static uint Packed(byte[] buttons, int button) => BinaryPrimitives.ReadUInt32LittleEndian(buttons.AsSpan(button * 4));

    private static async Task<(byte[] Buttons, CharacterRecord Record, IReadOnlyList<ActionButton> Stored)> CreateAndLoginAsync(
        WorldTestHost host, string account, byte race = 1)
    {
        byte[] key = await host.AddAccountAsync(account);
        await using WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync(account, key);
        await client.CreateCharacterAsync("Barwarrior", race);
        int id = (await host.Accounts.FindByUsernameAsync(account))!.Id;
        CharacterRecord record = (await host.Characters.GetByAccountAsync(id)).Single();
        IReadOnlyList<ActionButton> stored = await host.Characters.GetActionButtonsAsync(record.Id);
        await client.LoginAsync((ulong)record.Id);
        return (client.LoginPacket(WorldOpcode.SmsgActionButtons), record, stored);
    }

    [Fact]
    public async Task FirstLogin_SendsTheStartingBar()
    {
        (WorldTestHost host, FakeSource source) = Start();
        await using (host)
        {
            (byte[] buttons, _, IReadOnlyList<ActionButton> stored) = await CreateAndLoginAsync(host, "BAR1");

            Assert.Equal(480, buttons.Length);
            Assert.Equal(source.SpellA, Packed(buttons, 72));
            Assert.Equal(source.SpellB, Packed(buttons, 73));
            Assert.Equal(117u | (128u << 24), Packed(buttons, 83));
            Assert.Equal(3, Enumerable.Range(0, 120).Count(b => Packed(buttons, b) != 0)); // the button-130 row never reaches the bar
            Assert.Equal([72, 73, 83], stored.Select(b => (int)b.Button));
        }
    }

    [Fact]
    public async Task APairWithoutRows_StartsWithAnEmptyBar()
    {
        (WorldTestHost host, _) = Start();
        await using (host)
        {
            (byte[] buttons, _, IReadOnlyList<ActionButton> stored) = await CreateAndLoginAsync(host, "BAR2", race: 2);

            Assert.Empty(stored);
            Assert.All(Enumerable.Range(0, 120), b => Assert.Equal(0u, Packed(buttons, b)));
        }
    }

    [Fact]
    public async Task StartActionsFalse_AndLegacyMode_LeaveTheBarEmpty()
    {
        (WorldTestHost off, _) = Start(("StartActions", "false"));
        await using (off)
        {
            Assert.Empty((await CreateAndLoginAsync(off, "BAR3")).Stored);
        }

        (WorldTestHost legacy, _) = Start(("Mode", "Legacy"));
        await using (legacy)
        {
            Assert.Empty((await CreateAndLoginAsync(legacy, "BAR4")).Stored);
        }
    }

    [Fact]
    public async Task WritingTheBar_LeavesLevelAndMoneyOfTheNewCharacterAlone()
    {
        (WorldTestHost host, _) = Start(("StartPlayerLevel", "5"), ("StartPlayerMoney", "100"));
        await using (host)
        {
            (_, CharacterRecord record, IReadOnlyList<ActionButton> stored) = await CreateAndLoginAsync(host, "BAR5");

            Assert.Equal(3, stored.Count);
            Assert.Equal(((byte)5, 100u, 0u), (record.Level, record.Money, record.PlayedTime));
        }
    }

    [Fact]
    public void Filter_AppliesTheActionButtonDataChecks()
    {
        ActionButton[] rows =
        [
            new(0, 100, 0), // known spell
            new(1, 101, 0), // unknown spell
            new(2, 200, 128), // known item
            new(3, 201, 128), // unknown item
            new(4, 999, 64), // macro: never checked
            new(120, 100, 0), // button out of range (MAX_ACTION_BUTTONS)
            new(5, 0x01000000, 0), // action out of range (MAX_ACTION_BUTTON_ACTION_VALUE)
            new(6, 5, 7), // other types are not checked
        ];

        (IReadOnlyList<ActionButton> valid, int dropped) = StartActionsFeature.Filter(rows, id => id == 100, id => id == 200);

        Assert.Equal([0, 2, 4, 6], valid.Select(b => (int)b.Button));
        Assert.Equal(4, dropped);
    }

    [Fact]
    public void Filter_WithoutLoadedContent_KeepsTheRowsThatContentWouldJudge()
    {
        ActionButton[] rows = [new(0, 100, 0), new(1, 200, 128), new(120, 1, 0)];

        (IReadOnlyList<ActionButton> valid, int dropped) = StartActionsFeature.Filter(rows, spellExists: null, itemExists: null);

        Assert.Equal([0, 1], valid.Select(b => (int)b.Button));
        Assert.Equal(1, dropped); // the range check never depends on content
    }
}
