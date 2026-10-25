using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Characters;

/// <summary>
/// A client treats SMSG_INIT_WORLD_STATES (the last login packet) as the end of login, so the character's saves must already
/// be resumed when that packet is queued. This asserts the state at the moment of the send, which is deterministic; a test that
/// asserts after the client's login returns only fails when the server thread happens to be slow.
/// </summary>
public sealed class LoginResumesSavesBeforeWorldStatesTests
{
    [Fact]
    public async Task QuarantinedCharacter_IsResumedBeforeTheWorldStatesPacketIsSent()
    {
        var probe = new QuarantineProbe();
        await using var host = WorldTestHost.Start(configureServices: services =>
            services.AddSingleton<IOutboundPacketObserver>(probe));
        probe.IsQuarantined = host.SaveQueue.IsQuarantined;

        byte[] key = await host.AddAccountAsync("RESUME");
        await using WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync("RESUME", key);
        await client.CreateCharacterAsync("Resumer");
        Account account = (await host.Accounts.FindByUsernameAsync("RESUME"))!;
        CharacterRecord record = (await host.Characters.GetByAccountAsync(account.Id)).Single();
        probe.CharacterId = record.Id;

        host.SaveQueue.QuarantineCharacter(record.Id);
        Assert.True(host.SaveQueue.IsQuarantined(record.Id));
        await client.LoginAsync((ulong)record.Id);

        // Not vacuous: the packet was observed, and it was observed while the character was no longer quarantined.
        Assert.Equal(1, probe.WorldStatesSeen);
        Assert.False(probe.QuarantinedAtWorldStates, "SMSG_INIT_WORLD_STATES was sent while the character's saves were still quarantined");
        Assert.False(host.SaveQueue.IsQuarantined(record.Id));
    }

    private sealed class QuarantineProbe : IOutboundPacketObserver
    {
        private int _seen;
        private volatile bool _quarantined;

        public Func<int, bool> IsQuarantined { get; set; } = _ => false;

        public int CharacterId { get; set; }

        public int WorldStatesSeen => Volatile.Read(ref _seen);

        public bool QuarantinedAtWorldStates => _quarantined;

        public void Observe(WorldOpcode opcode, ReadOnlyMemory<byte> payload)
        {
            if (opcode == WorldOpcode.SmsgInitWorldStates)
            {
                _quarantined = IsQuarantined(CharacterId);
                Interlocked.Increment(ref _seen);
            }
        }
    }
}
