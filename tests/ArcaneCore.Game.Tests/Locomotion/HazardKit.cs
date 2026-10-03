using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.GridTerrain;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Tests.Locomotion;

/// <summary>
/// A world whose map 0 has real terrain: one tile around (30, -30) with ground at 20 and a liquid surface at 100 whose kind is
/// chosen by the test, plus a player standing in it. Test doubles for the combat random and the graveyard hook come with it.
/// </summary>
internal sealed class HazardKit : IDisposable
{
    public const float X = 30f;
    public const float Y = -30f;
    public const float Ground = 20f;
    public const float Level = 100f;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arcanecore-hazards-" + Guid.NewGuid().ToString("N"));

    public HazardKit(LiquidTypeFlags? liquid, params SpellInfo[] spells)
    {
        Directory.CreateDirectory(Path.Combine(_directory, "maps"));
        var builder = new MapFileBuilder { GridHeight = Ground };
        if (liquid is { } type)
        {
            builder.HasLiquid = true;
            builder.LiquidGlobalFlags = (byte)type;
            builder.LiquidGlobalEntry = 1;
            builder.LiquidLevel = Level;
        }

        File.WriteAllBytes(Path.Combine(_directory, "maps", TerrainTile.FileName(0, 31, 32)), builder.Build());
        Kit = new SpellTestKit(spells);
        Kit.World.Options.Maps.DataDirectory = _directory;
        Map = Kit.World.GetMap(0);
        Random = new ScriptedRandom();
        Hooks = new TestCombatHooks();
        Map.Combat.Random = Random;
        Map.Combat.Hooks = Hooks;
    }

    public SpellTestKit Kit { get; }

    public Map Map { get; }

    public ScriptedRandom Random { get; }

    public TestCombatHooks Hooks { get; }

    public Player AddPlayer(uint guid, float z, out FakeSession session, AccountSecurity security = AccountSecurity.Player)
    {
        session = new FakeSession((int)guid, security);
        Player player = TestWorld.CreatePlayer(guid, X, Y, session);
        player.Level = 60;
        player.MaxHealth = 3000;
        player.Health = 3000;
        player.Relocate(X, Y, z, 0, 1);
        Kit.World.AddPlayer(player);
        Kit.World.RunTick(0);
        session.Clear();
        return player;
    }

    /// <summary>Move a player the way a client does: one accepted movement block (observers around it).</summary>
    public void MoveTo(Player player, float z)
    {
        MovementInfo previous = player.Movement;
        var incoming = new MovementInfo { Flags = MovementFlags.Swimming, Time = 1, X = X, Y = Y, Z = z, Orientation = 0 };
        var context = new MovementObserverContext(player, Kit.World, WorldOpcode.MsgMoveHeartbeat);
        MovementObservers.Before(context, in previous, ref incoming);
        player.ApplyClientMovement(incoming, 1);
        MovementObservers.After(context, in previous);
    }

    public void Tick(uint diffMs) => Kit.World.RunTick(diffMs);

    public void Dispose()
    {
        Kit.Dispose();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
