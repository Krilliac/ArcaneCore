using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Scripting;
using ArcaneCore.Game.Tests.Duel;
using Xunit;

namespace ArcaneCore.Game.Tests.Scripting;

/// <summary>The global script hooks (AzerothCore ScriptMgr enabled-hook lists): registration, dispatch, isolation and the empty cost.</summary>
public sealed class ScriptHookRegistryTests
{
    private sealed class NothingOverridden : IPlayerHooks, IWorldHooks;

    private sealed class Counting : IPlayerHooks, IWorldHooks, IUnitHooks
    {
        public int Logins;
        public int Updates;
        public List<(Player Winner, Player Loser, DuelCompleteType Type)> Ends = [];
        public List<(Player A, Player B)> Starts = [];
        public bool AllowChat = true;

        public void OnLogin(Player player) => Logins++;

        public void OnUpdate(uint diffMs) => Updates++;

        public bool OnChat(Player player, ScriptChatMessage message) => AllowChat;

        public List<ScriptAddonMessage> Addon = [];
        public bool AllowAddon = true;

        public bool OnAddonMessage(Player player, ScriptAddonMessage message)
        {
            Addon.Add(message);
            return AllowAddon;
        }

        public void OnDuelStart(Player first, Player second) => Starts.Add((first, second));

        public void OnDuelEnd(Player winner, Player loser, DuelCompleteType type) => Ends.Add((winner, loser, type));

        public void OnDamage(Unit attacker, Unit victim, ref uint damage) => damage /= 2;
    }

    private sealed class Throwing : IWorldHooks
    {
        public void OnUpdate(uint diffMs) => throw new InvalidOperationException("boom");
    }

    private sealed class ThrowingAddon : IPlayerHooks
    {
        public int Calls;

        public bool OnAddonMessage(Player player, ScriptAddonMessage message)
        {
            Calls++;
            throw new InvalidOperationException("boom");
        }
    }

    [Fact]
    public void Register_RefusesAHookThatOverridesNothing_AndTheSameObjectTwice()
    {
        var registry = new ScriptHookRegistry();
        Assert.Throws<ArgumentException>(() => registry.Register(new NothingOverridden()));

        var hooks = new Counting();
        registry.Register(hooks);
        Assert.Throws<InvalidOperationException>(() => registry.Register(hooks));
        Assert.Single(registry.Registered);
    }

    [Fact]
    public void Register_AfterFreeze_Throws()
    {
        var registry = new ScriptHookRegistry();
        registry.Freeze();
        Assert.Throws<InvalidOperationException>(() => registry.Register(new Counting()));
    }

    [Fact]
    public void OnlyOverriddenMethods_JoinTheirHook()
    {
        var registry = new ScriptHookRegistry();
        registry.Register(new Counting());

        Assert.True(registry.Player.HasLogin);
        Assert.True(registry.Player.HasChat);
        Assert.False(registry.Player.HasLogout);
        Assert.False(registry.Item.HasEquipHooks);
        Assert.False(registry.Spell.HasHooks);
    }

    [Fact]
    public void AThrowingHook_DoesNotStopTheOthers()
    {
        var registry = new ScriptHookRegistry();
        var counting = new Counting();
        registry.Register(new Throwing());
        registry.Register(counting);

        registry.World.OnUpdate(50);

        Assert.Equal(1, counting.Updates);
    }

    [Fact]
    public void Chat_AnyFalseDropsTheLine_AndDamageIsModified()
    {
        var registry = new ScriptHookRegistry();
        var counting = new Counting { AllowChat = false };
        registry.Register(counting);
        using var rig = new DuelRig();

        Assert.False(registry.Player.OnChat(rig.A, new ScriptChatMessage(0, 0, "hi", null)));
        Assert.Equal(50u, registry.Unit.OnDamage(rig.A, rig.B, 100));
    }

    [Fact]
    public void AddonMessage_AnyFalseDropsTheLine_AndAThrowingHookIsLogged()
    {
        var registry = new ScriptHookRegistry();
        var throwing = new ThrowingAddon();
        var dropping = new Counting { AllowAddon = false };
        registry.Register(throwing);
        registry.Register(dropping);
        using var rig = new DuelRig();
        var message = ScriptAddonMessage.Parse(1, "PFX\tbody", null);

        Assert.True(registry.Player.HasAddonMessage);
        Assert.False(registry.Player.OnAddonMessage(rig.A, message));

        Assert.Equal(1, throwing.Calls);
        Assert.Equal(message, Assert.Single(dropping.Addon));
    }

    [Fact]
    public void EmptyDispatch_AllocatesNothing()
    {
        var registry = new ScriptHookRegistry();
        using var rig = new DuelRig();
        var message = new ScriptChatMessage(0, 0, "hi", null);
        var addonMessage = new ScriptAddonMessage(1, "PFX", "body", null);
        for (int warm = 0; warm < 2; warm++) Run();

        long before = GC.GetAllocatedBytesForCurrentThread();
        Run();
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());

        void Run()
        {
            for (int n = 0; n < 1000; n++)
            {
                registry.World.OnUpdate(50);
                registry.Player.OnLogin(rig.A);
                registry.Player.OnKill(rig.A, rig.B);
                _ = registry.Player.OnChat(rig.A, message);
                _ = registry.Player.OnAddonMessage(rig.A, addonMessage);
                _ = registry.Unit.OnDamage(rig.A, rig.B, 10);
                registry.Unit.OnDeath(rig.B, rig.A);
                registry.Player.OnDuelEnd(rig.A, rig.B, DuelCompleteType.Won);
            }
        }
    }

    [Fact]
    public void WorldDispatch_DuelStartAndEnd_ReachTheHooks()
    {
        using var rig = new DuelRig();
        var counting = new Counting();
        rig.World.Scripts.Register(counting);
        rig.Challenge();
        rig.AcceptAndStart();

        (Player first, Player second) = Assert.Single(counting.Starts);
        Assert.Equal([rig.A.Guid, rig.B.Guid], new[] { first.Guid, second.Guid }.OrderBy(g => g.Value));

        rig.Service.Cancel(rig.B); // B forfeits: B lost, A won
        var end = Assert.Single(counting.Ends);
        Assert.Same(rig.A, end.Winner);
        Assert.Same(rig.B, end.Loser);
        Assert.Equal(DuelCompleteType.Won, end.Type);
    }

    [Fact]
    public void WorldDispatch_TickAndLogin_ReachTheHooks()
    {
        using var rig = new DuelRig();
        var counting = new Counting();
        rig.World.Scripts.Register(counting);

        rig.Tick();
        rig.World.NotifyLoggedIn(rig.A);

        Assert.Equal(1, counting.Updates);
        Assert.Equal(1, counting.Logins);
    }
}
