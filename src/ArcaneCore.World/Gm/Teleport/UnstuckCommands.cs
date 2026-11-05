using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Features;
using ArcaneCore.World.Graveyards;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Gm.Teleport;

/// <summary>
/// <c>.unstuck</c> (vmangos HandleUnstuckCommand, TeleportCommands.cpp:1068-1105; SEC_PLAYER, Chat.cpp:1268). A living player gets
/// Undying Soul (20939), a dummy aura; when it expires the player dies, with resurrection sickness, and releases to a graveyard as
/// usual (<see cref="UnstuckFeature"/>, vmangos SpellAuras.cpp:1919-1944). A ghost gets the sickness, a one-hour Undying Soul cooldown
/// and a teleport to the nearest graveyard at once.
/// <para>
/// Differences: without a linked graveyard the expiry still kills (vmangos then sets health to 1 and sends the player to Westfall or
/// the Barrens); the ghost's cooldown is server side only.
/// </para>
/// </summary>
public sealed class UnstuckCommands : ICommandGroup
{
    /// <summary>SPELL_UNDYING_SOUL (TeleportCommands.cpp:1065).</summary>
    public const uint UndyingSoul = 20939;

    /// <summary>The cooldown a ghost's unstuck starts (HOUR * IN_MILLISECONDS).</summary>
    public const long GhostCooldownMs = 60 * 60 * 1000;

    /// <summary>The lowest level allowed to unstuck.</summary>
    public const int MinLevel = 10;

    /// <summary>vmangos mangos_string 67 (LANG_UNSTUCK_UNAVAILABLE).</summary>
    public const string Unavailable = "You may not use the unstuck command at this time.";

    /// <summary>vmangos mangos_string 68 (LANG_UNSTUCK_ALIVE).</summary>
    public const string Alive = "Unstuck activated. You will die once the buff expires.\nDo not enter combat or cast spells, or it will be cancelled.\nPlease note that any use of this command is logged.";

    /// <summary>vmangos mangos_string 69 (LANG_UNSTUCK_DEAD).</summary>
    public const string Dead = "The unstuck function has been successfully activated.\nYou have been teleported to the nearest graveyard.\nPlease note that any use of this command is logged.";

    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand("unstuck", AccountSecurity.Player,
            "Syntax: .unstuck\nIf you are stuck: alive, you die once Undying Soul expires; as a ghost, you are sent to the nearest graveyard.",
            Unstuck, RetailLevel: 0),
    ];

    private static bool Unstuck(CommandContext context, string text)
    {
        TryApply(context.Session.Services, context.Player, out string reply);
        context.Reply(reply);
        return true;
    }

    /// <summary>
    /// The unstuck rules and effect for <paramref name="player"/> (world thread), shared by <c>.unstuck</c> and the GM ticket assistant.
    /// False with <see cref="Unavailable"/> when the rules refuse (nothing is changed); otherwise true with <see cref="Alive"/> (Undying
    /// Soul cast, death when it expires) or <see cref="Dead"/> (a ghost: sickness, the cooldown and the graveyard at once).
    /// </summary>
    internal static bool TryApply(IServiceProvider services, Player player, out string reply)
    {
        SpellSystem? spells = services.GetService<SpellFeature>()?.System;
        SpellInfo? undying = spells?.Store.Get(UndyingSoul);
        if (spells is null || undying is null
            || player.Combat.IsInCombat
            || BattlegroundManager.TypeOfMap(player.MapId) != BattlegroundType.None
            || (player.UnitFlags & UnitFlags.TaxiFlight) != 0
            || !spells.IsSpellReady(player, undying)
            || player.Combat.DeathState == DeathState.Corpse
            || player.Level < MinLevel)
        {
            reply = Unavailable;
            return false;
        }

        if (player.IsAlive)
        {
            if (spells.CastSpell(player, UndyingSoul, SpellCastTargets.ForSelf(), triggered: false) != SpellCastResult.CastOk)
            {
                reply = Unavailable;
                return false;
            }

            reply = Alive;
        }
        else
        {
            UnstuckFeature.ApplySickness(spells, player);
            spells.RestoreCooldowns(player, [new PersistedCooldown(SpellCooldownKind.Spell, UndyingSoul,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + GhostCooldownMs)], DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            services.GetService<GraveyardFeature>()?.Service?.RepopAtGraveyard(player);
            reply = Dead;
        }

        services.GetService<ILoggerFactory>()?.CreateLogger<UnstuckCommands>().LogInformation(
            "Player {Name} (guid {Guid}) used unstuck command at map {Map} ({X}, {Y}, {Z}).",
            player.Name, player.Guid.Low, player.MapId, player.X, player.Y, player.Z);
        return true;
    }
}

/// <summary>The expiry of Undying Soul (vmangos SpellAuras.cpp:1919-1944, Aura::HandleAuraDummy): an unstuck player dies.</summary>
public sealed class UnstuckFeature(IServiceProvider services) : IWorldFeature
{
    private WorldRuntime? _world;

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        _world = world;
        world.Post(() =>
        {
            if (services.GetService<SpellFeature>()?.System is { } spells)
            {
                spells.HolderRemoved += holder => OnHolderRemoved(spells, holder);
            }
        });
    }

    internal static void ApplySickness(SpellSystem spells, Player player)
    {
        if (spells.Store.Get(ArcaneCore.Game.Npc.SpiritHealerResurrection.ResurrectionSicknessSpell) is not null)
        {
            spells.CastSpell(player, ArcaneCore.Game.Npc.SpiritHealerResurrection.ResurrectionSicknessSpell, SpellCastTargets.ForSelf(), triggered: true);
        }
    }

    private void OnHolderRemoved(SpellSystem spells, SpellAuraHolder holder)
    {
        if (holder.Spell.Id != UnstuckCommands.UndyingSoul || holder.RemoveMode != AuraRemoveMode.Expire
            || holder.Target is not Player player || holder.CasterGuid != player.Guid)
        {
            return;
        }

        // Not inside the aura removal: the death removes auras of its own.
        _world?.Post(() =>
        {
            if (!player.IsInWorld || !player.IsAlive || player.Combat.IsInCombat || (player.UnitFlags & UnitFlags.TaxiFlight) != 0
                || player.Map is not { } map)
            {
                return;
            }

            ApplySickness(spells, player);
            map.Combat.Kill(null, player, durabilityLoss: false);
        });
    }
}