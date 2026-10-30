using ArcaneCore.Game.Maps;
using ArcaneCore.Protocol;
using ArcaneCore.World.Features;
using ArcaneCore.World.Packets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.ServerMail;

/// <summary>Configuration section "AutoBroadcast" (vmangos AutoBroadcast.Timer, World.cpp:739).</summary>
public sealed class AutoBroadcastOptions
{
    public const string SectionName = "AutoBroadcast";

    /// <summary>Off by default: ClassicDB has no autobroadcast table, so the messages come from this section.</summary>
    public bool Enabled { get; set; }

    /// <summary>vmangos default 1800000 (30 minutes).</summary>
    public uint IntervalMs { get; set; } = 1_800_000;

    /// <summary>The announcements (vmangos autobroadcast.string_id rows; sql/custom/autobroadcast_example.sql has the classic tips).</summary>
    public List<string> Messages { get; set; } = [];
}

/// <summary>
/// vmangos AutoBroadCastMgr (src/game/AutoBroadCastMgr.cpp): every interval, one message picked at random is sent to every player as a
/// system message (World::SendWorldText). Nothing happens with no messages.
/// </summary>
public sealed class AutoBroadcastFeature(IServiceProvider services, ILogger<AutoBroadcastFeature>? logger = null)
    : IWorldFeature, Cluster.Social.IRealmSingletonHalt
{
    private uint _current;
    private volatile bool _halted;

    /// <summary>Whether <see cref="Halt"/> was called: every later tick sends nothing.</summary>
    public bool Halted => _halted;

    /// <summary>Stop broadcasting for good (a lost social realm authority lease). Safe from any thread.</summary>
    public void Halt()
    {
        if (_halted) return;
        _halted = true;
        logger?.LogWarning("AutoBroadcast halted: the node no longer holds the social realm authority lease");
    }

    public AutoBroadcastOptions Options { get; } = Bind(services.GetService<IConfiguration>());

    internal Random Random { get; set; } = Random.Shared;

    public static AutoBroadcastOptions Bind(IConfiguration? configuration)
    {
        var options = new AutoBroadcastOptions();
        configuration?.GetSection(AutoBroadcastOptions.SectionName).Bind(options);
        options.Messages.RemoveAll(string.IsNullOrWhiteSpace);
        return options;
    }

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (!Options.Enabled || Options.Messages.Count == 0 || Options.IntervalMs == 0) return;
        logger?.LogInformation("Loaded {Count} AutoBroadCast messages", Options.Messages.Count);
        world.WorldTick += diff =>
        {
            if (_halted) return;
            if (Update(diff) is { } message)
                world.BroadcastToAll(WorldOpcode.SmsgMessagechat, ChatPackets.BuildSystemMessage(message));
        };
    }

    /// <summary>AutoBroadCastMgr::Update: the message due after <paramref name="diffMs"/>, or null.</summary>
    internal string? Update(uint diffMs)
    {
        if (_halted || Options.Messages.Count == 0) return null;
        _current += diffMs;
        if (_current < Options.IntervalMs) return null;
        _current = 0;
        return Options.Messages[Random.Next(Options.Messages.Count)];
    }
}
