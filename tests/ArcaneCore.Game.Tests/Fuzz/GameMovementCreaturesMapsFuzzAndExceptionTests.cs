using System.Numerics;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Instances;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.World;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Grid;
using ArcaneCore.Game.Maps.SpawnGroups;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Game.Scripting;
using ArcaneCore.Game.Scripting.Modules;
using ArcaneCore.Game.Tests.Creatures;
using ArcaneCore.Game.Tests.Transports;
using ArcaneCore.Game.Transports;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.SpawnGroups;
using ArcaneCore.Protocol;
using Xunit;
using GridCoord = ArcaneCore.Game.Maps.Grid.GridCoord;

namespace ArcaneCore.Game.Tests.Fuzz;

/// <summary>
/// Exception guard and deterministic seeded fuzz tests across <c>Locomotion</c>, <c>Transports</c>,
/// <c>Creatures</c> (AI, threat, formations, waypoint/escort movement, splines, smoothing),
/// <c>Pets</c>, <c>GameObjects</c>, <c>Scripting</c>, <c>Maps/Grid</c>, and <c>Instances</c>.
/// </summary>
public sealed class GameMovementCreaturesMapsFuzzAndExceptionTests
{
    // =========================================================================
    // 1. Locomotion — Exception Tests & Seeded Fuzz
    // =========================================================================

    [Fact]
    public void Locomotion_NullAndOutOfRangeGuards_ThrowExpectedExceptions()
    {
        WorldRuntime world = TestWorld.CreateRuntime();
        Player player = TestWorld.CreatePlayer(1u, 0f, 0f, new FakeSession(1));
        var options = new LocomotionOptions();

        // LocomotionEnvironment guards
        Assert.Throws<ArgumentNullException>(() => new LocomotionEnvironment(null!));
        Assert.Throws<ArgumentNullException>(() => LocomotionEnvironment.Register(null!, LocomotionEnvironment.Default));
        Assert.Throws<ArgumentNullException>(() => LocomotionEnvironment.Register(world, null!));
        Assert.Throws<ArgumentNullException>(() => LocomotionEnvironment.RegisterMitigation(null!, NoEnvironmentalMitigation.Instance));
        Assert.Throws<ArgumentNullException>(() => LocomotionEnvironment.RegisterMitigation(world, null!));
        Assert.Throws<ArgumentNullException>(() => LocomotionEnvironment.RegisterSpellBridge(null!, new DummySpellBridge()));
        Assert.Throws<ArgumentNullException>(() => LocomotionEnvironment.RegisterSpellBridge(world, null!));
        Assert.Throws<ArgumentNullException>(() => LocomotionEnvironment.RegisterMountDisplays(null!, new DummyMountDisplaySource()));
        Assert.Throws<ArgumentNullException>(() => LocomotionEnvironment.RegisterMountDisplays(world, null!));
        Assert.Throws<ArgumentNullException>(() => LocomotionEnvironment.RegisterFallModifiers(null!, NoFallDamageModifiers.Instance));
        Assert.Throws<ArgumentNullException>(() => LocomotionEnvironment.RegisterFallModifiers(world, null!));
        Assert.Throws<ArgumentNullException>(() => LocomotionEnvironment.FallModifiersFor(null!));
        Assert.Throws<ArgumentNullException>(() => LocomotionEnvironment.MitigationFor(null!));
        Assert.Throws<ArgumentNullException>(() => LocomotionEnvironment.RegisteredOptions(null!));
        Assert.Throws<ArgumentNullException>(() => LocomotionEnvironment.For((WorldRuntime)null!));

        // LocomotionStates / LocomotionState / AuraLedger guards
        Assert.Throws<ArgumentNullException>(() => LocomotionStates.TryGet(null!, out _));
        var state = new LocomotionState();
        Assert.Throws<ArgumentNullException>(() => state.StartSpline(null!));
        Assert.Throws<ArgumentNullException>(() => state.SampleSpline(null!));
        Assert.Throws<ArgumentNullException>(() => state.Auras.Add(null!));
        Assert.Throws<ArgumentNullException>(() => state.Auras.Remove(null!));

        // MovementObservers / LiquidEnvironment / EnvironmentalDamage / KnockbackService
        Assert.Throws<ArgumentNullException>(() => MovementObservers.Register(null!, new FallObserver()));
        Assert.Throws<ArgumentNullException>(() => MovementObservers.Register(world, null!));
        Assert.Throws<ArgumentNullException>(() => LiquidEnvironment.UpdateTerrainFlags(null!));
        Assert.Throws<ArgumentNullException>(() => EnvironmentalDamage.Apply(null!, player, EnvironmentalDamageType.Lava, 10u));
        Assert.Throws<ArgumentNullException>(() => EnvironmentalDamage.Apply(world, null!, EnvironmentalDamageType.Lava, 10u));
        MovementInfo moveInfo = default;
        Assert.Throws<ArgumentNullException>(() => KnockbackService.Acknowledge(null!, 1u, in moveInfo));

        // ForcedMovement guards
        Assert.Throws<ArgumentNullException>(() => ForcedMovement.ContactPoint(null!, player, 1f));
        Assert.Throws<ArgumentNullException>(() => ForcedMovement.ContactPoint(player, null!, 1f));
        Assert.Throws<ArgumentNullException>(() => ForcedMovement.PointInFront(null!, player, 2f));
        Assert.Throws<ArgumentNullException>(() => ForcedMovement.PointInFront(player, null!, 2f));
        Assert.Throws<ArgumentNullException>(() => ForcedMovement.LaunchSpline(null!, [Vector3.Zero], 10f));
        Assert.Throws<ArgumentNullException>(() => ForcedMovement.LaunchSpline(player, null!, 10f));
        Assert.Throws<ArgumentNullException>(() => ForcedMovement.ChargeHasNoPath(null!, player));
        Assert.Throws<ArgumentNullException>(() => ForcedMovement.ChargeHasNoPath(player, null!));
        Assert.Throws<ArgumentNullException>(() => ForcedMovement.ChargePath(null!, Vector3.Zero));
        Assert.Throws<ArgumentNullException>(() => ForcedMovement.LeapDestination(null!, 10f));
        Assert.Throws<ArgumentNullException>(() => ForcedMovement.BearingFromTo(null!, player));
        Assert.Throws<ArgumentNullException>(() => ForcedMovement.BearingFromTo(player, null!));

        // MovementControl / SpeedRates / UnitSpeed guards
        Assert.Throws<ArgumentNullException>(() => MovementControl.Request(null!, MovementChangeType.Root, true));
        Assert.Throws<ArgumentNullException>(() => MovementControl.Order(null!, MovementChangeType.Root, true));
        Assert.Throws<ArgumentNullException>(() => MovementControl.ApplyReal(null!, MovementChangeType.Root, true));
        Assert.Throws<ArgumentNullException>(() => MovementControl.Acknowledge(null!, MovementChangeType.Root, 1u, true));
        Assert.Throws<ArgumentNullException>(() => MovementControl.AcknowledgeSpeed(null!, MoveType.Run, 1u, 7f));
        Assert.Throws<ArgumentNullException>(() => MovementControl.Enforce(null!, new PendingMovementChange(1u, MovementChangeType.Root, true), false));
        Assert.Throws<ArgumentNullException>(() => MovementControl.Enforce(player, null!, false));
        Assert.Throws<ArgumentNullException>(() => MovementControl.RelayToObservers(null!, MovementChangeType.Root, true));

        Assert.Throws<ArgumentNullException>(() => SpeedRates.Apply(null!, PlayerSpeedRates.Retail, options));
        Assert.Throws<ArgumentNullException>(() => SpeedRates.Apply(player, PlayerSpeedRates.Retail, null!));
        Assert.Throws<ArgumentNullException>(() => SpeedRates.Refresh(null!, options));
        Assert.Throws<ArgumentNullException>(() => SpeedRates.Refresh(player, null!));
        Assert.Throws<ArgumentNullException>(() => SpeedRates.ApplyToAll(null!, options));
        Assert.Throws<ArgumentNullException>(() => SpeedRates.ApplyToAll(world, null!));

        Assert.Throws<ArgumentNullException>(() => UnitSpeed.Get(null!, MoveType.Run));
        Assert.Throws<ArgumentNullException>(() => UnitSpeed.SetReal(null!, MoveType.Run, 7f));
        Assert.Throws<ArgumentNullException>(() => UnitSpeed.IsMounted(null!));
        Assert.Throws<ArgumentNullException>(() => UnitSpeed.ComputeRate(null!, MoveType.Run, options, false));
        Assert.Throws<ArgumentNullException>(() => UnitSpeed.ComputeRate(player, MoveType.Run, null!, false));

        // Enum range guards on MovementChangeInfo, SpeedPackets, PlayerSpeedRates, UnitSpeed
        Assert.Throws<ArgumentOutOfRangeException>(() => MovementChangeInfo.FlagOf(MovementChangeType.SpeedRun));
        Assert.Throws<ArgumentOutOfRangeException>(() => MovementChangeInfo.ControllerOpcode(MovementChangeType.KnockBack, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => MovementChangeInfo.ObserverOpcode(MovementChangeType.SpeedWalk, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => MovementChangeInfo.EnforcedOpcode((MovementChangeType)999, true));

        var badMoveType = (MoveType)99;
        Assert.Throws<ArgumentOutOfRangeException>(() => SpeedPackets.ForceOpcode(badMoveType));
        Assert.Throws<ArgumentOutOfRangeException>(() => SpeedPackets.AckOpcode(badMoveType));
        Assert.Throws<ArgumentOutOfRangeException>(() => SpeedPackets.ObserverOpcode(badMoveType));
        Assert.Throws<ArgumentOutOfRangeException>(() => SpeedPackets.SplineOpcode(badMoveType));
        Assert.Throws<ArgumentOutOfRangeException>(() => PlayerSpeedRates.Retail.For(badMoveType));
        Assert.Throws<ArgumentOutOfRangeException>(() => UnitSpeed.BaseSpeed(badMoveType));
        Assert.Throws<ArgumentOutOfRangeException>(() => UnitSpeed.Get(player, badMoveType));
        Assert.Throws<ArgumentOutOfRangeException>(() => UnitSpeed.SetReal(player, badMoveType, 1f));
        Assert.Throws<ArgumentOutOfRangeException>(() => UnitSpeed.ChangeTypeOf(badMoveType));
        Assert.Throws<ArgumentOutOfRangeException>(() => UnitSpeed.MoveTypeOf(MovementChangeType.Root));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(20251004)]
    public void Locomotion_SeededFuzz_PendingAcksMirrorTimersAndFallDamagePreserveInvariants(int seed)
    {
        var rng = new Random(seed);
        var pending = new PendingMovementChanges();
        MovementChangeType[] allChangeTypes = Enum.GetValues<MovementChangeType>();

        uint counter = 1;
        for (int step = 0; step < 250; step++)
        {
            int op = rng.Next(6);
            if (op == 0)
            {
                MovementChangeType type = allChangeTypes[rng.Next(allChangeTypes.Length)];
                bool apply = rng.Next(2) == 0;
                float speed = (float)(rng.NextDouble() * 20.0);
                KnockbackInfo? kb = type == MovementChangeType.KnockBack
                    ? new KnockbackInfo(MathF.Cos(step), MathF.Sin(step), speed, -speed * 0.5f)
                    : null;
                PendingMovementChange pushed = pending.Push(counter, type, apply, speed, kb);
                Assert.Equal(counter, pending.LastCounterOf(type));
                Assert.True(pending.HasPendingOfType(type));
                Assert.Equal(counter, pushed.Counter);
                counter++;
            }
            else if (op == 1 && pending.Count > 0)
            {
                PendingMovementChange pick = pending.Changes[rng.Next(pending.Count)];
                bool corrupt = rng.Next(3) == 0;
                uint c = corrupt ? pick.Counter + 999u : pick.Counter;
                bool ok = pending.TryAcknowledge(c, pick.Type, pick.Apply);
                if (!corrupt)
                {
                    Assert.True(ok);
                }
            }
            else if (op == 2 && pending.Count > 0)
            {
                PendingMovementChange pick = pending.Changes[rng.Next(pending.Count)];
                float delta = rng.Next(2) == 0 ? 0.005f : 0.5f;
                bool ok = pending.TryAcknowledgeSpeed(pick.Counter, pick.Type, pick.NewValue + delta);
                Assert.Equal(delta <= 0.01f, ok);
            }
            else if (op == 3)
            {
                pending.Age((uint)rng.Next(0, 2500));
            }
            else if (op == 4)
            {
                uint ackTimeout = (uint)rng.Next(500, 5000);
                bool teleporting = rng.Next(2) == 0;
                PendingMovementChange? enforced = pending.CheckTimeout(ackTimeout, teleporting, out bool dropped);
                if (enforced is not null)
                {
                    Assert.False(dropped);
                    Assert.Equal(enforced.Counter, pending.LastCounterOf(enforced.Type));
                }
            }
            else if (op == 5 && rng.Next(10) == 0)
            {
                IReadOnlyList<PendingMovementChange> resolved = pending.ResolveAll();
                Assert.False(pending.HasPending);
                Assert.Equal(0, pending.Count);
                Assert.NotNull(resolved);
            }

            // Fall damage calculator invariants
            float fallDistance = (float)(rng.NextDouble() * 120.0 - 20.0);
            int safeFallBonus = rng.Next(-20, 50);
            float takenMod = (float)(rng.NextDouble() * 2.5);
            uint maxHealth = (uint)rng.Next(1, 50000);
            float rate = (float)(rng.NextDouble() * 3.0);
            uint dmg = FallDamageCalculator.Damage(fallDistance, safeFallBonus, maxHealth, rate, takenMod);
            if (fallDistance < FallDamageCalculator.MinFallDistance || rate <= 0f || takenMod <= 0f)
            {
                Assert.Equal(0u, dmg);
            }
            else
            {
                Assert.InRange(dmg, 0u, maxHealth);
            }
        }

        // MirrorTimer fuzzing
        var timer = new MirrorTimer(MirrorTimerType.Breath);
        for (int i = 0; i < 150; i++)
        {
            int op = rng.Next(6);
            switch (op)
            {
                case 0:
                    timer.SetScale(rng.Next(-5, 6));
                    break;
                case 1:
                    timer.Start((uint)rng.Next(1, 60000), (uint)rng.Next(0, 100));
                    break;
                case 2:
                    timer.SetDuration((uint)rng.Next((int)timer.Duration, (int)timer.Duration + 10000));
                    break;
                case 3:
                    timer.SetRemaining((uint)rng.Next(0, (int)Math.Max(1u, timer.Duration)));
                    break;
                case 4:
                    timer.SetFrozen(rng.Next(2) == 0);
                    break;
                case 5:
                    _ = timer.Update((uint)rng.Next(0, 5000));
                    break;
            }

            _ = timer.FetchStatus();
            Assert.True(timer.Remaining <= timer.Duration);
        }

        // LocomotionOptions.Normalize idempotence under fuzzed values
        var opts = new LocomotionOptions
        {
            RateDamageFall = rng.Next(3) == 0 ? float.NaN : (float)(rng.NextDouble() * 20.0 - 5.0),
            EnvironmentalDamageMin = (uint)rng.Next(0, 1000),
            EnvironmentalDamageMax = (uint)rng.Next(0, 1000),
            PlayerSpeedRate = rng.Next(3) == 0 ? float.NaN : (float)(rng.NextDouble() * 30.0 - 5.0),
            PlayerRunSpeedRate = (float)(rng.NextDouble() * 25.0 - 2.0),
            PlayerWalkSpeedRate = (float)(rng.NextDouble() * 25.0 - 2.0),
            PlayerTurnRate = (float)(rng.NextDouble() * 25.0 - 2.0),
        };
        _ = opts.Normalize();
        Assert.True(opts.RateDamageFall >= 0f);
        Assert.True(opts.EnvironmentalDamageMax >= opts.EnvironmentalDamageMin);
        Assert.InRange(opts.PlayerSpeedRate, LocomotionOptions.MinSpeedRate, LocomotionOptions.MaxSpeedRate);
        Assert.Empty(opts.Normalize());
    }

    // =========================================================================
    // 2. Transports — Exception Tests & Seeded Fuzz
    // =========================================================================

    [Fact]
    public void Transports_NullAndInvalidInputs_ThrowExpectedExceptions()
    {
        WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(0);
        var session = new FakeSession(1);
        Player player = TestWorld.CreatePlayer(1u, 0f, 0f, session);

        // TransportSpline guards
        Assert.Throws<ArgumentException>(() => TransportSpline.CatmullRom(ReadOnlySpan<Vector3>.Empty));
        Assert.Throws<ArgumentException>(() => TransportSpline.CatmullRom([Vector3.Zero]));
        Assert.Throws<ArgumentNullException>(() => TransportSpline.Raw(null!));
        Assert.Throws<ArgumentException>(() => TransportSpline.Raw([Vector3.Zero, Vector3.One, Vector3.UnitX]));

        TransportSpline spline = TransportSpline.CatmullRom([Vector3.Zero, new Vector3(10f, 0f, 0f), new Vector3(20f, 10f, 0f)]);
        Assert.Throws<ArgumentOutOfRangeException>(() => spline.Evaluate(spline.First - 1, 0.5f));
        Assert.Throws<ArgumentOutOfRangeException>(() => spline.Evaluate(spline.Last, 0.5f));
        Assert.Throws<ArgumentOutOfRangeException>(() => spline.Derivative(spline.First - 1, 0.5f));
        Assert.Throws<ArgumentOutOfRangeException>(() => spline.Derivative(spline.Last, 0.5f));

        // TransportTemplateBuilder guards
        var goTemplate = new GameObjectTemplate { Entry = 1, Type = (uint)GameObjectType.MoTransport, Name = "Ship" };
        var catalog = new TaxiPathNodeCatalog([]);
        Assert.Throws<ArgumentNullException>(() => TransportTemplateBuilder.Build(null!, catalog, _ => false, null, out _));
        Assert.Throws<ArgumentNullException>(() => TransportTemplateBuilder.Build(goTemplate, null!, _ => false, null, out _));
        Assert.Throws<ArgumentNullException>(() => TransportTemplateBuilder.Build(goTemplate, catalog, null!, null, out _));

        // TransportPackets guards
        Assert.Throws<ArgumentNullException>(() => TransportPackets.BuildCreate(null!, player, 0u));
        Assert.Throws<ArgumentNullException>(() => TransportPackets.BuildCreate([], null!, 0u));
        Assert.Throws<ArgumentNullException>(() => TransportPackets.BuildOutOfRange(null!));
        Assert.Throws<ArgumentNullException>(() => TransportPackets.Send(null!, null, 100));

        // TransportSystem & ShipTransport guards
        Assert.Throws<ArgumentNullException>(() => new TransportSystem(null!, []));
        Assert.Throws<ArgumentNullException>(() => new TransportSystem(world, null!));
        Assert.Throws<ArgumentNullException>(() => TransportSystem.Register(null!, new TransportSystem(world, [])));
        Assert.Throws<ArgumentNullException>(() => TransportSystem.Register(world, null!));
        Assert.Throws<ArgumentNullException>(() => TransportSystem.Of(null!));

        TransportSystem system = TransportTestKit.Install(world, TransportTestKit.Ferry, TransportTestKit.Crossing);
        ShipTransport ship = system.FindByEntry(TransportTestKit.Ferry)!;
        Assert.NotNull(ship);

        Assert.Throws<ArgumentNullException>(() => system.Find(null!, ship.Guid));
        Assert.Throws<ArgumentNullException>(() => system.ResendTo(null!, player));
        Assert.Throws<ArgumentNullException>(() => system.ResendTo(ship, null!));

        Assert.Throws<ArgumentNullException>(() => ship.AddPassenger(null!));
        Assert.Throws<ArgumentNullException>(() => ship.RemovePassenger(null!));
        Assert.Throws<ArgumentNullException>(() => ship.UpdatePassengerPosition(null!));
        Assert.Throws<ArgumentNullException>(() => ship.AddFollower(null!, player));
        Assert.Throws<ArgumentNullException>(() => ship.AddFollower(player, null!));
        Assert.Throws<ArgumentNullException>(() => ship.RemoveFollower(null!, player));
        Assert.Throws<ArgumentNullException>(() => ship.RemoveFollower(player, null!));

        _ = map;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(20251004)]
    public void Transports_SeededFuzz_CatmullRomAndPassengerCoordinateRoundTrip(int seed)
    {
        var rng = new Random(seed);
        int count = rng.Next(2, 18);
        var controls = new Vector3[count];
        Vector3 cursor = Vector3.Zero;
        for (int i = 0; i < count; i++)
        {
            cursor += new Vector3(
                (float)(rng.NextDouble() * 40.0 + 2.0),
                (float)(rng.NextDouble() * 40.0 - 20.0),
                (float)(rng.NextDouble() * 10.0 - 5.0));
            controls[i] = cursor;
        }

        TransportSpline spline = TransportSpline.CatmullRom(controls);
        Assert.Equal(1, spline.First);
        Assert.Equal(count, spline.Last);
        Assert.True(spline.TotalLength > 0.0);

        double prevLen = 0.0;
        for (int seg = spline.First; seg < spline.Last; seg++)
        {
            double cumLen = spline.Length(spline.First, seg + 1);
            Assert.True(cumLen >= prevLen);
            prevLen = cumLen;

            for (int sample = 0; sample <= 4; sample++)
            {
                float u = sample * 0.25f;
                Vector3 pos = spline.Evaluate(seg, u);
                Vector3 deriv = spline.Derivative(seg, u);
                Assert.True(float.IsFinite(pos.X) && float.IsFinite(pos.Y) && float.IsFinite(pos.Z));
                Assert.True(float.IsFinite(deriv.X) && float.IsFinite(deriv.Y) && float.IsFinite(deriv.Z));
            }
        }

        // Passenger world <-> local offset round-trip & orientation normalization
        for (int i = 0; i < 100; i++)
        {
            float rawO = (float)(rng.NextDouble() * 40.0 - 20.0);
            float normO = TransportTemplateBuilder.NormalizeOrientation(rawO);
            Assert.InRange(normO, 0f, 2f * MathF.PI);

            float tx = (float)(rng.NextDouble() * 4000.0 - 2000.0);
            float ty = (float)(rng.NextDouble() * 4000.0 - 2000.0);
            float tz = (float)(rng.NextDouble() * 400.0 - 200.0);
            float to = normO;

            float lx = (float)(rng.NextDouble() * 40.0 - 20.0);
            float ly = (float)(rng.NextDouble() * 40.0 - 20.0);
            float lz = (float)(rng.NextDouble() * 10.0 - 5.0);
            float lo = TransportTemplateBuilder.NormalizeOrientation((float)(rng.NextDouble() * 6.28));

            float x = lx, y = ly, z = lz, o = lo;
            ShipTransport.CalculatePassengerPosition(ref x, ref y, ref z, ref o, tx, ty, tz, to);
            ShipTransport.CalculatePassengerOffset(ref x, ref y, ref z, ref o, tx, ty, tz, to);

            Assert.True(MathF.Abs(lx - x) < 1e-2f);
            Assert.True(MathF.Abs(ly - y) < 1e-2f);
            Assert.True(MathF.Abs(lz - z) < 1e-2f);
        }
    }

    // =========================================================================
    // 3. Creatures (AI, Threat, Formations, Splines, Smoothing, Movement)
    // =========================================================================

    [Fact]
    public void Creatures_NullAndPreconditionGuards_ThrowExpectedExceptions()
    {
        WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(0);
        Map map1 = world.GetMap(1);
        Player player = TestWorld.CreatePlayer(1u, 0f, 0f, new FakeSession(1));
        CreatureTemplate template = CreatureTestSupport.Template(entry: 100u);

        // Creature constructor & methods
        Assert.ThrowsAny<Exception>(() => new Creature(1u, null!, null, CreatureContent.Empty, new Random(1)));
        Assert.Throws<ArgumentNullException>(() => new Creature(1u, null!, null, CreatureContent.Empty, new Random(1), guidEntry: 1u));
        Assert.Throws<ArgumentNullException>(() => new Creature(1u, template, null, null!, new Random(1)));
        Assert.Throws<ArgumentNullException>(() => new Creature(1u, template, null, CreatureContent.Empty, null!));

        var creature = new Creature(1u, template, null, CreatureContent.Empty, new Random(1));
        Assert.Throws<ArgumentNullException>(() => creature.ReceiveEmote(null!, 1u));
        Assert.Throws<ArgumentNullException>(() => creature.ReceiveAiEvent(1u, null!, null));
        Assert.Throws<ArgumentNullException>(() => creature.StartSpline(null!, false, SplineFacing.None, 1u, 0L));
        Assert.Throws<ArgumentException>(() => creature.StartSpline([], false, SplineFacing.None, 1u, 0L));
        Assert.Throws<ArgumentNullException>(() => creature.ChangeTemplate(null!));

        // MotionMaster without movement owner throws InvalidOperationException on Push
        Assert.Throws<ArgumentNullException>(() => creature.Motion.MoveChase(null!));
        Assert.Throws<ArgumentNullException>(() => creature.Motion.MoveFollow(null!, 2f, 0f));
        Assert.Throws<InvalidOperationException>(() => creature.Motion.MoveChase(player));
        Assert.Throws<InvalidOperationException>(() => creature.Motion.MovePoint(1u, 10f, 10f, 0f, true));

        // CreatureContent.Empty guards
        Assert.Throws<ArgumentNullException>(() => CreatureContent.Empty.SwapDefinitions(null!));
        Assert.Throws<InvalidOperationException>(() => CreatureContent.Empty.SwapDefinitions(CreatureContent.Empty));

        // CreatureMovePackets guards
        Assert.Throws<ArgumentNullException>(() => CreatureMovePackets.BuildPath(creature.Guid, Vector3.Zero, 1u, SplineFacing.None, false, 100u, null!));
        Assert.Throws<ArgumentException>(() => CreatureMovePackets.BuildPath(creature.Guid, Vector3.Zero, 1u, SplineFacing.None, false, 100u, []));
        var invalidFacing = new SplineFacing((MonsterMoveType)99, 0f, Vector3.Zero, ObjectGuid.Empty);
        Assert.Throws<ArgumentOutOfRangeException>(() => CreatureMovePackets.BuildPath(creature.Guid, Vector3.Zero, 1u, invalidFacing, false, 100u, [Vector3.One]));

        // CreatureSplineSmoothing guards
        var mesh = new ConfigurableMeshPointQuery(available: true, hit: false);
        List<Vector3> corners = [new Vector3(5f, 0f, 0f), new Vector3(10f, 5f, 0f)];
        Assert.Throws<ArgumentNullException>(() => CreatureSplineSmoothing.VaryAndSmooth(null!, Vector3.Zero, corners, mesh, (_, _, _, z) => z));
        Assert.Throws<ArgumentNullException>(() => CreatureSplineSmoothing.VaryAndSmooth(creature, Vector3.Zero, null!, mesh, (_, _, _, z) => z));
        Assert.Throws<ArgumentNullException>(() => CreatureSplineSmoothing.VaryAndSmooth(creature, Vector3.Zero, corners, null!, (_, _, _, z) => z));
        Assert.Throws<ArgumentNullException>(() => CreatureSplineSmoothing.VaryAndSmooth(creature, Vector3.Zero, corners, mesh, null!));
        Assert.Throws<ArgumentNullException>(() => CreatureSplineSmoothing.SmoothCorners(0u, Vector3.Zero, null!, mesh, (_, _, _, z) => z));
        Assert.Throws<ArgumentNullException>(() => CreatureSplineSmoothing.SmoothCorners(0u, Vector3.Zero, corners, null!, (_, _, _, z) => z));
        Assert.Throws<ArgumentNullException>(() => CreatureSplineSmoothing.SmoothCorners(0u, Vector3.Zero, corners, mesh, null!));

        // FormationState guards
        var formationEntry = new SpawnGroupFormation((byte)FormationShape.SingleFile, 3f, 0u, 0u, 0, string.Empty);
        Assert.Throws<ArgumentNullException>(() => new FormationState(1u, null!, []));
        Assert.Throws<ArgumentNullException>(() => new FormationState(1u, formationEntry, null!));
        var formationState = new FormationState(1u, formationEntry, [new SpawnGroupMember(1u, 0, 0u)]);
        Assert.Throws<ArgumentNullException>(() => formationState.FixSlotsPositions(1f, null!));

        // CreatureAI / FactionCreatureHostility / CreatureAiFactory / ThreatList guards
        Assert.Throws<ArgumentNullException>(() => new NullCreatureAI(null!));
        Assert.Throws<ArgumentNullException>(() => new ReactorAI(null!));
        Assert.Throws<ArgumentNullException>(() => new AggressorAI(null!));
        Assert.Throws<ArgumentNullException>(() => FactionCreatureHostility.Empty.IsHostile(null!, player));
        Assert.Throws<ArgumentNullException>(() => FactionCreatureHostility.Empty.IsHostile(creature, null!));
        Assert.Throws<ArgumentNullException>(() => FactionCreatureHostility.Empty.IsFriendly(null!, player));
        Assert.Throws<ArgumentNullException>(() => FactionCreatureHostility.Empty.IsFriendly(creature, null!));
        Assert.Throws<ArgumentNullException>(() => FactionCreatureHostility.Empty.IsHostileToPlayers(null!));

        var aiFactory = new CreatureAiFactory();
        Assert.Throws<ArgumentException>(() => aiFactory.Register("", c => new NullCreatureAI(c)));
        Assert.Throws<ArgumentNullException>(() => aiFactory.Register("custom_ai", null!));
        Assert.Throws<ArgumentNullException>(() => aiFactory.RegisterEntryScript(999999u, null!));
        Assert.Throws<ArgumentException>(() => aiFactory.RegisterEventAiScript("", (c, ai) => new CreatureEventAI(c, ai)));
        Assert.Throws<ArgumentNullException>(() => aiFactory.RegisterEventAiScript("custom_event_ai", null!));
        Assert.Throws<ArgumentException>(() => aiFactory.RegisterNpcScript("", "Impl"));
        Assert.Throws<ArgumentException>(() => aiFactory.RegisterNpcScript("custom_npc", "Impl", gossip: null, quest: null));
        Assert.Throws<ArgumentNullException>(() => aiFactory.ValidateSwap(null!, [], [], []));
        Assert.Throws<ArgumentNullException>(() => aiFactory.ValidateSwap([], null!, [], []));
        Assert.Throws<ArgumentNullException>(() => aiFactory.Create(null!, CreatureContent.Empty, out _));

        Assert.Throws<ArgumentNullException>(() => creature.Combat.Threat.AddThreat(null!, 10f));
        Assert.Throws<ArgumentNullException>(() => creature.Combat.Threat.GetTauntTarget(null!, _ => true));
        Assert.Throws<ArgumentNullException>(() => creature.Combat.Threat.GetTauntTarget(_ => null, null!));
        Assert.Throws<ArgumentNullException>(() => creature.Combat.Threat.SelectVictim(null!, _ => true));
        Assert.Throws<ArgumentNullException>(() => creature.Combat.Threat.SelectVictim(_ => true, null!));

        // CreatureMapSystem guards
        Assert.Throws<ArgumentNullException>(() => new CreatureMapSystem(null!, CreatureContent.Empty));
        Assert.Throws<ArgumentNullException>(() => new CreatureMapSystem(map, null!));
        var cms = new CreatureMapSystem(map, CreatureContent.Empty);
        Assert.Throws<InvalidOperationException>(() => cms.Update(map1, 50u));
        Assert.Throws<ArgumentNullException>(() => cms.MovePath(null!, [Vector3.One], false, SplineFacing.None));
        Assert.Throws<ArgumentNullException>(() => cms.MovePath(creature, null!, false, SplineFacing.None));
        Assert.Throws<ArgumentNullException>(() => cms.StopMoving(null!));
        Assert.Throws<ArgumentNullException>(() => cms.CollectCreaturesNear(0f, 0f, 10f, null!));
        Assert.Throws<ArgumentNullException>(() => MapCreatureHeightProvider.Floor(null!, 0f, 0f, 0f));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(20251004)]
    public void Creatures_SeededFuzz_SplineEvaluationSmoothingFormationsWaypointsAndThreat(int seed)
    {
        var rng = new Random(seed);
        CreatureTemplate template = CreatureTestSupport.Template(entry: 200u);
        var creature = new Creature((uint)(seed & 0xFFFF) + 1u, template, null, CreatureContent.Empty, new Random(seed));

        // 1. CreatureSpline & CreatureSplineSmoothing fuzzing
        int cornerCount = rng.Next(2, 14);
        var rawCorners = new List<Vector3>(cornerCount);
        Vector3 pos = Vector3.Zero;
        for (int i = 0; i < cornerCount; i++)
        {
            float angle = (float)(rng.NextDouble() * MathF.PI * 1.5 - MathF.PI * 0.75);
            float stepLen = (float)(rng.NextDouble() * 8.0 + 3.0);
            pos += new Vector3(stepLen * MathF.Cos(angle), stepLen * MathF.Sin(angle), (float)(rng.NextDouble() * 0.5 - 0.25));
            rawCorners.Add(pos);
        }

        Vector3 expectedEnd = rawCorners[^1];
        var clearMesh = new ConfigurableMeshPointQuery(available: true, hit: false);
        List<Vector3> smoothed = CreatureSplineSmoothing.VaryAndSmooth(
            creature,
            Vector3.Zero,
            [.. rawCorners],
            clearMesh,
            (_, _, _, z) => z,
            allowVariation: true);

        Assert.NotEmpty(smoothed);
        Assert.True(smoothed.Count <= CreatureSplineSmoothing.MaxSplinePoints);
        Assert.Equal(expectedEnd, smoothed[^1]);
        foreach (Vector3 p in smoothed)
        {
            Assert.True(float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z));
        }

        // Blocked mesh raycast preserves original corners untouched
        var blockedMesh = new ConfigurableMeshPointQuery(available: true, hit: true);
        List<Vector3> unsmoothed = CreatureSplineSmoothing.VaryAndSmooth(
            creature,
            Vector3.Zero,
            [.. rawCorners],
            blockedMesh,
            (_, _, _, z) => z,
            allowVariation: true);
        Assert.Equal(rawCorners, unsmoothed);

        // CreatureSpline evaluation at arbitrary timestamps
        uint durationMs = (uint)rng.Next(1, 20000);
        var cSpline = new CreatureSpline(1u, 0f, 0f, 0f, expectedEnd.X, expectedEnd.Y, expectedEnd.Z, Run: true, FinalOrientation: 1.5f, StartClockMs: 1000L, DurationMs: durationMs)
        {
            Path = smoothed,
            Facing = SplineFacing.ToAngle(1.5f),
        };

        Assert.Equal((0f, 0f, 0f), cSpline.PositionAt(500L));
        Assert.Equal((expectedEnd.X, expectedEnd.Y, expectedEnd.Z), cSpline.PositionAt(1000L + durationMs + 500L));
        for (int s = 0; s < 25; s++)
        {
            long clock = 1000L + rng.Next(-500, (int)durationMs + 1000);
            (float sx, float sy, float sz, float so) = cSpline.PositionAndOrientationAt(clock, 0.5f);
            Assert.True(float.IsFinite(sx) && float.IsFinite(sy) && float.IsFinite(sz) && float.IsFinite(so));
            Assert.InRange(so, 0f, 2f * MathF.PI);
        }

        // 2. FormationShape & FormationState slot calculation fuzzing
        FormationShape[] shapes = Enum.GetValues<FormationShape>();
        foreach (FormationShape shape in shapes)
        {
            int membersCount = rng.Next(1, 10);
            var members = new List<SpawnGroupMember>(membersCount);
            for (int m = 0; m < membersCount; m++)
            {
                members.Add(new SpawnGroupMember((uint)(m + 1), m, 0u));
            }

            float spread = (float)(rng.NextDouble() * 6.0 + 1.0);
            var entry = new SpawnGroupFormation((byte)shape, spread, 0u, 0u, 0, string.Empty);
            var fState = new FormationState(10u, entry, members);
            fState.FixSlotsPositions((float)(rng.NextDouble() * 3.0), new Random(seed));

            for (int m = 0; m < membersCount; m++)
            {
                FormationSlot? slot = fState.DefaultSlotOf((uint)(m + 1));
                Assert.NotNull(slot);
                Assert.True(float.IsFinite(slot.Angle) && float.IsFinite(slot.Distance));
                Assert.True(slot.Distance >= 0f);
            }
        }

        // 3. WaypointMovementGenerator loop & linear ping-pong fuzzing
        var waypoints = new List<CreatureWaypoint>
        {
            new(1u, 10f, 0f, 0f, 100f, 50u),
            new(2u, 20f, 0f, 0f, 1.0f, 0u),
            new(3u, 30f, 10f, 0f, 100f, 25u),
        };
        var mover = new InstantMover(seed);
        var wpGen = new WaypointMovementGenerator(waypoints, linear: rng.Next(2) == 0, startIndex: rng.Next(0, 5));
        wpGen.Initialize(creature, mover);
        Assert.InRange(wpGen.CurrentIndex, 0, waypoints.Count - 1);

        for (int tick = 0; tick < 30; tick++)
        {
            creature.StopSpline(0L, creature.Movement.Time);
            Assert.True(wpGen.Update(creature, mover, (uint)rng.Next(10, 100)));
            Assert.InRange(wpGen.CurrentIndex, 0, waypoints.Count - 1);
        }

        // 4. ThreatList sorted order & 110%/130% hysteresis fuzzing
        ThreatList threat = creature.Combat.Threat;
        var targets = new Player[6];
        for (int i = 0; i < targets.Length; i++)
        {
            targets[i] = TestWorld.CreatePlayer((uint)(i + 10), 0f, 0f, new FakeSession(i + 10));
        }

        for (int step = 0; step < 120; step++)
        {
            Player t = targets[rng.Next(targets.Length)];
            switch (rng.Next(7))
            {
                case 0:
                    threat.AddThreat(t, (float)(rng.NextDouble() * 200.0 - 30.0));
                    break;
                case 1:
                    threat.ModifyThreatPercent(t, rng.Next(-120, 150));
                    break;
                case 2:
                    threat.ScaleThreat(t, rng.Next(-150, 150));
                    break;
                case 3:
                    threat.ApplyTempThreatModifier(t, (float)(rng.NextDouble() * 100.0 - 50.0));
                    break;
                case 4:
                    threat.ResetTempThreat(t);
                    break;
                case 5:
                    threat.SetOnlineState(t, rng.Next(3) != 0);
                    break;
                case 6:
                    _ = threat.SelectVictim(_ => true, u => (u.Guid.Counter & 1) == 0);
                    break;
            }

            IReadOnlyList<ThreatEntry> online = threat.Entries;
            for (int i = 1; i < online.Count; i++)
            {
                Assert.True(online[i - 1].Threat >= online[i].Threat);
                Assert.True(online[i].Threat >= 0f);
            }
        }
    }

    // =========================================================================
    // 4. Pets & GameObjects — Exception Tests & Seeded Fuzz
    // =========================================================================

    [Fact]
    public void PetsAndGameObjects_NullAndInvariantGuards_ThrowExpectedExceptions()
    {
        WorldRuntime world = TestWorld.CreateRuntime();
        Map map0 = world.GetMap(0);
        Map map1 = world.GetMap(1);
        Player player = TestWorld.CreatePlayer(1u, 0f, 0f, new FakeSession(1));
        CreatureTemplate cTemplate = CreatureTestSupport.Template(entry: 300u);
        var pet = new Creature(10u, cTemplate, null, CreatureContent.Empty, new Random(1), HighGuid.Pet, guidEntry: 1u);

        // SummonService / PetController / PetStableController / PetLoyalty / CharmService
        Assert.Throws<ArgumentNullException>(() => new PetMapSystem(null!, world));
        Assert.Throws<ArgumentNullException>(() => new PetMapSystem(map0, null!));
        var summons = new SummonService();
        Assert.Throws<ArgumentNullException>(() => summons.Install(null!));
        Assert.Throws<ArgumentNullException>(() => summons.InstallDemons(null!));
        Assert.Throws<ArgumentNullException>(() => summons.Unsummon(null!));
        Assert.Throws<ArgumentNullException>(() => summons.Summon(null!, 1u, 0f, 0f, 0f, 0f, 1000));
        Assert.Throws<ArgumentNullException>(() => summons.GivePetXp(null!, 100u));
        Assert.Throws<ArgumentNullException>(() => summons.AwardKillExperience(null!, pet, false, 1f, 1f));
        Assert.Throws<ArgumentNullException>(() => summons.AwardKillExperience([player], null!, false, 1f, 1f));
        Assert.Throws<ArgumentNullException>(() => summons.ModifyLoyaltyByCommand(null!, player, 100));
        Assert.Throws<ArgumentNullException>(() => summons.ModifyLoyaltyByCommand(pet, null!, 100));
        Assert.Throws<ArgumentNullException>(() => summons.LearnSpellByCommand(null!, 1u));
        Assert.Throws<ArgumentNullException>(() => summons.UnlearnSpellByCommand(null!, 1u));
        Assert.Throws<ArgumentNullException>(() => summons.SetPetEnabled(null!, true));
        Assert.Throws<ArgumentNullException>(() => summons.AutoReSummonPet(null!));
        Assert.Throws<ArgumentNullException>(() => summons.ForgetOwner(null!));
        Assert.Throws<ArgumentNullException>(() => summons.CheckTaming(null!, pet));
        Assert.Throws<ArgumentNullException>(() => summons.TameCreature(null!, pet, 1u));
        Assert.Throws<ArgumentNullException>(() => summons.TameCreature(player, null!, 1u));
        Assert.Throws<ArgumentNullException>(() => summons.UnsummonPetTemporarily(null!));
        Assert.Throws<ArgumentNullException>(() => summons.ResummonTemporarilyUnsummonedPet(null!));
        Assert.Throws<ArgumentNullException>(() => summons.ForgetTemporarilyUnsummonedPet(null!));
        Assert.Throws<ArgumentNullException>(() => summons.QueueDeletePet(null!));

        Assert.Throws<ArgumentNullException>(() => new PetController(null!));
        var petController = new PetController(summons);
        Assert.Throws<ArgumentNullException>(() => petController.UntrainablePet(null!));
        Assert.Throws<ArgumentNullException>(() => petController.HandleUnlearn(null!, pet.Guid));

        var stableController = new PetStableController(summons);
        Assert.Throws<ArgumentNullException>(() => stableController.OpenAsGm(null!));
        Assert.Throws<ArgumentNullException>(() => stableController.Prefetch(null!));

        Assert.Throws<ArgumentNullException>(() => PetLoyalty.Restore(null!, 1, 0, 0));
        Assert.Throws<ArgumentNullException>(() => PetLoyalty.Modify(null!, 100));
        Assert.Throws<ArgumentNullException>(() => PetLoyalty.GivePetLevel(null!, 2));

        var charmService = new CharmService();
        Assert.Throws<ArgumentNullException>(() => charmService.Install(null!));
        Assert.Throws<ArgumentNullException>(() => charmService.Uncharm(null!));
        Assert.Throws<ArgumentNullException>(() => charmService.RemoveCharmAuras(null!));
        Assert.Throws<ArgumentNullException>(() => CharmService.UpdateControl(null!));
        Assert.Throws<ArgumentNullException>(() => new MapUnitControl(null!, world));
        Assert.Throws<ArgumentNullException>(() => new MapUnitControl(map0, null!));
        var unitControl = new MapUnitControl(map0, world);
        Assert.Throws<ArgumentNullException>(() => unitControl.Schedule(null!, 10u, static () => { }));
        Assert.Throws<ArgumentNullException>(() => unitControl.Schedule(player, 10u, null!));

        // GameObject & GameObjectMapSystem guards
        var goTemplate = new GameObjectTemplate { Entry = 500u, Type = (uint)GameObjectType.Door, Name = "Door" };
        var goDiffEntry = new GameObjectTemplate { Entry = 501u, Type = (uint)GameObjectType.Door, Name = "OtherDoor" };
        Assert.ThrowsAny<Exception>(() => new GameObject(1u, null!, null));
        var go = new GameObject(1u, goTemplate, null);
        Assert.Throws<ArgumentNullException>(() => go.ReplaceTemplate(null!));
        Assert.Throws<ArgumentException>(() => go.ReplaceTemplate(goDiffEntry));
        Assert.Throws<ArgumentNullException>(() => go.DistanceTo(null!));

        Assert.Throws<ArgumentNullException>(() => GameObjectChairs.ClosestSlot(null!, 0f, 0f));
        Assert.Throws<ArgumentNullException>(() => GameObjectChairs.Height(null!));
        Assert.Throws<ArgumentNullException>(() => GameObjectInfoView.AutoCloseSeconds(null!));
        Assert.Throws<ArgumentNullException>(() => GameObjectInfoView.CooldownSeconds(null!));
        Assert.Throws<ArgumentNullException>(() => GameObjectInfoView.Charges(null!));
        Assert.Throws<ArgumentNullException>(() => GameObjectInfoView.LinkedTrapEntry(null!));
        Assert.Throws<ArgumentNullException>(() => GameObjectInfoView.IsDespawnAtAction(null!));
        Assert.Throws<ArgumentNullException>(() => GameObjectInfoView.DespawnPossibility(null!));
        Assert.Throws<ArgumentNullException>(() => GameObjectInfoView.CannotBeUsedUnderImmunity(null!));
        Assert.Throws<ArgumentNullException>(() => GameObjectInfoView.IsUsableMounted(null!));
        Assert.Throws<ArgumentNullException>(() => GameObjectInfoView.NeverDespawns(null!, 10));
        Assert.Throws<ArgumentNullException>(() => GameObjectLocks.LockIdOf(null!));
        Assert.Throws<ArgumentNullException>(() => GameObjectLocks.CheckDirectUse(null, null!));
        Assert.Throws<ArgumentNullException>(() => GameObjectLocks.CheckOpenLock(null, null!, LockType.PickLock, 0u, (_, _) => 0u));
        Assert.Throws<ArgumentNullException>(() => GameObjectLocks.CheckOpenLock(null, player, LockType.PickLock, 0u, null!));
        Assert.Throws<ArgumentNullException>(() => GameObjectPackets.QueryResponse(null!));
        Assert.Throws<ArgumentNullException>(() => GameObjectReactions.IsHostileTo(null!, player, null));
        Assert.Throws<ArgumentNullException>(() => GameObjectReactions.IsHostileTo(go, null!, null));
        Assert.Throws<ArgumentNullException>(() => GameObjectReactions.IsFriendlyTo(null!, player, null));
        Assert.Throws<ArgumentNullException>(() => GameObjectReactions.CanHelp(null!, player, null));

        Assert.Throws<ArgumentNullException>(() => new GameObjectMapSystem(null!, GameObjectContent.Empty));
        Assert.Throws<ArgumentNullException>(() => new GameObjectMapSystem(map0, null!));
        var goms = new GameObjectMapSystem(map0, GameObjectContent.Empty);
        Assert.Throws<InvalidOperationException>(() => goms.Update(map1, 50u));
        Assert.Throws<ArgumentNullException>(() => goms.ReplaceContent(null!));
        Assert.Throws<ArgumentNullException>(() => goms.RegisterAi(500u, null!));
        Assert.Throws<ArgumentNullException>(() => goms.AiFor(null!));
        Assert.Throws<ArgumentNullException>(() => goms.Use(null!, go.Guid));
        Assert.Throws<ArgumentNullException>(() => goms.RegisterUseHandler(GameObjectType.Door, null!));
        Assert.Throws<ArgumentNullException>(() => goms.UseByUnit(null!, go));
        Assert.Throws<ArgumentNullException>(() => goms.UseByUnit(player, null!));
        Assert.Throws<ArgumentNullException>(() => goms.ToggleDoorOrButton(null!));
        Assert.Throws<ArgumentNullException>(() => goms.Inspect(null!));
        Assert.Throws<ArgumentNullException>(() => goms.Remove(null!));
        Assert.Throws<ArgumentNullException>(() => goms.Despawn(null!));
        Assert.Throws<ArgumentNullException>(() => goms.ForceRespawn(null!));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(20251004)]
    public void PetsAndGameObjects_SeededFuzz_CharmActionBarAndChairSlots(int seed)
    {
        var rng = new Random(seed);

        // CharmInfo spell learning, rank replacement, and action bar fuzzing
        var charm = new CharmInfo(ReactState.Defensive);
        charm.InitPetActionBar();
        Assert.Equal(CharmInfo.ActionBarSize, charm.ActionBar.Count);

        for (int i = 0; i < 80; i++)
        {
            uint spellId = (uint)rng.Next(1, 12);
            int op = rng.Next(5);
            if (op == 0)
            {
                _ = charm.LearnSpell(spellId, rng.Next(2) == 0 ? ActionType.Disabled : ActionType.Enabled);
            }
            else if (op == 1)
            {
                _ = charm.UnlearnSpell(spellId);
                Assert.False(charm.HasSpell(spellId));
            }
            else if (op == 2)
            {
                charm.SetSpellAutocast(spellId, rng.Next(2) == 0);
            }
            else if (op == 3)
            {
                int slot = rng.Next(-2, CharmInfo.ActionBarSize + 2);
                charm.SetActionBar(slot, spellId, (byte)ActionType.Enabled);
                _ = charm.GetButton(slot);
            }
            else
            {
                _ = charm.ReplaceRank(spellId, spellId + 100u);
                charm.ClearFlags();
            }
        }

        // GameObjectChairs.ClosestSlot & GameObjectInfoView fuzzing
        for (int i = 0; i < 60; i++)
        {
            var data = new uint[GameObjectTemplate.DataCount];
            data[0] = (uint)rng.Next(0, 12); // chair slots
            data[1] = (uint)rng.Next(0, 5);  // chair height
            data[2] = (uint)rng.Next(0, 0x50000);
            uint type = (uint)rng.Next(0, 26);

            var template = new GameObjectTemplate
            {
                Entry = (uint)(1000 + i),
                Type = type,
                Size = (float)(rng.NextDouble() * 3.0 + 0.1),
                Data = data,
            };

            var chair = new GameObject((uint)(i + 1), template, null);
            chair.Relocate(
                (float)(rng.NextDouble() * 200.0 - 100.0),
                (float)(rng.NextDouble() * 200.0 - 100.0),
                0f,
                (float)(rng.NextDouble() * MathF.PI * 2f));

            float ux = chair.X + (float)(rng.NextDouble() * 10.0 - 5.0);
            float uy = chair.Y + (float)(rng.NextDouble() * 10.0 - 5.0);
            (float sx, float sy) = GameObjectChairs.ClosestSlot(chair, ux, uy);
            Assert.True(float.IsFinite(sx) && float.IsFinite(sy));
            Assert.InRange(GameObjectChairs.Height(template), (byte)0, (byte)2);

            _ = template.AutoCloseSeconds();
            _ = template.CooldownSeconds();
            _ = template.Charges();
            _ = template.LinkedTrapEntry();
            _ = template.IsDespawnAtAction();
            _ = template.DespawnPossibility();
            _ = template.CannotBeUsedUnderImmunity();
            _ = template.IsUsableMounted();
            _ = template.NeverDespawns(rng.Next(-10, 60));
        }
    }

    // =========================================================================
    // 5. Maps/Grid, Instances, and Scripting — Exception Tests & Seeded Fuzz
    // =========================================================================

    [Fact]
    public void MapsGridInstancesAndScripting_NullAndStateGuards_ThrowExpectedExceptions()
    {
        WorldRuntime world = TestWorld.CreateRuntime();
        Map map0 = world.GetMap(0);
        Player p1 = TestWorld.CreatePlayer(1u, 10f, 10f, new FakeSession(1));

        // GridContainer state guards
        var grid = new GridContainer(new MapOptions { GridCleanUpDelayMs = 300_000 }, 100f);
        Assert.Throws<InvalidOperationException>(() => grid.SetActive(p1, true));
        grid.Add(p1, active: false);
        Assert.Throws<InvalidOperationException>(() => grid.Add(p1, active: true));
        grid.Remove(p1);
        Assert.Throws<InvalidOperationException>(() => grid.SetActive(p1, false));

        // InstanceScriptRegistry & InstanceManager guards
        var instRegistry = new InstanceScriptRegistry();
        Assert.Throws<ArgumentNullException>(() => new DummyInstanceData(null!));
        Assert.Throws<ArgumentNullException>(() => instRegistry.Register(36u, null!));
        instRegistry.Register(36u, m => new DummyInstanceData(m));
        Assert.Throws<ArgumentNullException>(() => instRegistry.Create(null!));
        Assert.Throws<ArgumentNullException>(() => instRegistry.RunsCurrentScript(null!));
        Assert.Throws<ArgumentNullException>(() => instRegistry.ValidateSwap(null!, new HashSet<uint>()));
        Assert.Throws<ArgumentNullException>(() => instRegistry.ValidateSwap(new HashSet<uint>(), null!));
        Assert.Throws<ArgumentNullException>(() => instRegistry.CommitSwap(null!, new Dictionary<uint, Func<Map, InstanceData>>()));
        Assert.Throws<ArgumentNullException>(() => instRegistry.CommitSwap(new HashSet<uint>(), null!));
        Assert.Throws<InvalidOperationException>(() => instRegistry.CommitSwap(new HashSet<uint> { 999u }, new Dictionary<uint, Func<Map, InstanceData>>()));
        Assert.Throws<ArgumentNullException>(() => instRegistry.Restore(null!));
        Assert.Throws<ArgumentException>(() => instRegistry.Restore(new InstanceScriptRegistry().Save()));
        Assert.Throws<ArgumentNullException>(() => InstanceScriptRegistry.Discover((System.Reflection.Assembly)null!));
        Assert.Throws<ArgumentNullException>(() => InstanceScriptRegistry.DiscoverFactories(null!));

        Assert.Throws<ArgumentNullException>(() => new EasternKingdomsWorldMapData(null!));
        Assert.Throws<ArgumentNullException>(() => new KalimdorWorldMapData(null!));
        Assert.Throws<ArgumentNullException>(() => WorldMapData.Create(null!));
        Assert.Throws<ArgumentNullException>(() => InstanceManager.InstanceDataOf(null!));

        // Scripting: ScriptAddonMessage, ScriptHookRegistry, DuelResetScript
        Assert.Throws<ArgumentNullException>(() => ScriptAddonMessage.Parse(1u, null!, null));

        var hookRegistry = new ScriptHookRegistry();
        Assert.Throws<ArgumentNullException>(() => hookRegistry.Register(null!));
        Assert.Throws<ArgumentException>(() => hookRegistry.Register(new NoOpScriptHook()));
        var validHook = new CountingWorldHook();
        hookRegistry.Register(validHook);
        Assert.Throws<InvalidOperationException>(() => hookRegistry.Register(validHook));
        Assert.Throws<ArgumentNullException>(() => hookRegistry.ValidateSwap(null!, []));
        Assert.Throws<ArgumentNullException>(() => hookRegistry.ValidateSwap([], null!));
        Assert.Throws<ArgumentNullException>(() => hookRegistry.CommitSwap(null!, []));
        Assert.Throws<ArgumentNullException>(() => hookRegistry.CommitSwap([], null!));
        hookRegistry.Freeze();
        Assert.True(hookRegistry.IsFrozen);
        Assert.Throws<InvalidOperationException>(() => hookRegistry.Register(new CountingWorldHook()));

        Assert.Throws<ArgumentNullException>(() => new DuelResetScript(null!, () => null));
        Assert.Throws<ArgumentNullException>(() => new DuelResetScript(new DuelResetSettings(), null!));
        var duelReset = new DuelResetScript(new DuelResetSettings { Cooldowns = true }, () => null);
        Assert.Throws<ArgumentNullException>(() => duelReset.Apply(null!));
        Assert.Throws<ArgumentNullException>(() => duelReset.IsAllowedInArea(null!));

        _ = map0;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(20251004)]
    public void MapsGridAndScripting_SeededFuzz_GridContainerJoinOrderAndAddonParsing(int seed)
    {
        var rng = new Random(seed);
        var grid = new GridContainer(new MapOptions { GridCleanUpDelayMs = 60_000 }, 100f);
        var activeInGrid = new HashSet<Player>();
        var allPlayers = new Player[16];
        for (int i = 0; i < allPlayers.Length; i++)
        {
            allPlayers[i] = TestWorld.CreatePlayer((uint)(i + 1), 0f, 0f, new FakeSession(i + 1));
            allPlayers[i].MapSequence = i + 1;
        }

        var collectedObjects = new List<WorldObject>();
        var extraObjects = new List<WorldObject>();
        var collectedPlayers = new List<Player>();
        var extraPlayers = new List<Player>();

        for (int step = 0; step < 200; step++)
        {
            Player p = allPlayers[rng.Next(allPlayers.Length)];
            int op = rng.Next(5);
            if (op == 0 && !activeInGrid.Contains(p))
            {
                float x = (float)(rng.NextDouble() * 2000.0 - 1000.0);
                float y = (float)(rng.NextDouble() * 2000.0 - 1000.0);
                p.Relocate(x, y, 0f, 0f);
                grid.Add(p, active: rng.Next(2) == 0);
                activeInGrid.Add(p);
            }
            else if (op == 1 && activeInGrid.Contains(p))
            {
                float x = (float)(rng.NextDouble() * 2000.0 - 1000.0);
                float y = (float)(rng.NextDouble() * 2000.0 - 1000.0);
                p.Relocate(x, y, 0f, 0f);
                grid.Relocate(p);
            }
            else if (op == 2 && activeInGrid.Contains(p))
            {
                grid.SetActive(p, rng.Next(2) == 0);
            }
            else if (op == 3 && activeInGrid.Contains(p))
            {
                Assert.True(grid.Remove(p));
                activeInGrid.Remove(p);
            }
            else
            {
                grid.MarkActiveCells();
                float qx = (float)(rng.NextDouble() * 1000.0 - 500.0);
                float qy = (float)(rng.NextDouble() * 1000.0 - 500.0);
                float qr = (float)(rng.NextDouble() * 600.0);
                collectedObjects.Clear();
                extraObjects.Clear();
                collectedPlayers.Clear();
                extraPlayers.Clear();
                Assert.True(grid.TryCollectObjectsInJoinOrder(qx, qy, qr, collectedObjects, extraObjects, out _));
                Assert.True(grid.TryCollectPlayersInJoinOrder(qx, qy, qr, collectedPlayers, extraPlayers, out _));

                for (int i = 1; i < collectedObjects.Count; i++)
                {
                    Assert.True(collectedObjects[i - 1].MapSequence < collectedObjects[i].MapSequence);
                }

                for (int i = 1; i < collectedPlayers.Count; i++)
                {
                    Assert.True(collectedPlayers[i - 1].MapSequence < collectedPlayers[i].MapSequence);
                }
            }
        }

        // GridDefines coordinate clamping & validity fuzzing
        for (int i = 0; i < 100; i++)
        {
            float x = rng.Next(5) == 0 ? float.NaN : (float)(rng.NextDouble() * 40000.0 - 20000.0);
            float y = rng.Next(5) == 0 ? float.PositiveInfinity : (float)(rng.NextDouble() * 40000.0 - 20000.0);
            GridCoord gc = GridDefines.ComputeGridCoord(x, y);
            CellCoord cc = GridDefines.ComputeCellCoord(x, y);
            CellArea area = GridDefines.CalculateCellArea(x, y, (float)(rng.NextDouble() * 500.0 - 50.0));

            Assert.InRange(gc.X, 0, GridDefines.MaxNumberOfGrids - 1);
            Assert.InRange(gc.Y, 0, GridDefines.MaxNumberOfGrids - 1);
            Assert.InRange(cc.X, 0, GridDefines.TotalNumberOfCellsPerMap - 1);
            Assert.InRange(cc.Y, 0, GridDefines.TotalNumberOfCellsPerMap - 1);
            Assert.True(area.Low.X <= area.High.X);
            Assert.True(area.Low.Y <= area.High.Y);
        }

        // ScriptAddonMessage.Parse first-tab splitting invariant
        for (int i = 0; i < 60; i++)
        {
            string left = $"Pfx{rng.Next(0, 1000)}";
            string right = $"Body	With	ExtraTabs_{rng.Next(0, 1000)}";
            ScriptAddonMessage withTab = ScriptAddonMessage.Parse(1u, $"{left}	{right}", "Target");
            Assert.Equal(left, withTab.Prefix);
            Assert.Equal(right, withTab.Text);

            ScriptAddonMessage noTab = ScriptAddonMessage.Parse(2u, left, null);
            Assert.Null(noTab.Prefix);
            Assert.Equal(left, noTab.Text);
        }
    }

    // =========================================================================
    // Test Doubles & Helpers
    // =========================================================================

    private sealed class DummySpellBridge : IEnvironmentSpellBridge
    {
        public void RemoveAurasWithInterruptFlags(Unit unit, ArcaneCore.Game.Spells.SpellAuraInterruptFlags flags) { }
        public void InterruptChannelsWithFlags(Unit unit, ArcaneCore.Game.Spells.SpellAuraInterruptFlags flags) { }
    }

    private sealed class DummyMountDisplaySource : IMountDisplaySource
    {
        public uint? FindMountDisplay(uint creatureEntry) => creatureEntry;
    }

    private sealed class ConfigurableMeshPointQuery(bool available, bool hit) : IMeshPointQuery
    {
        public bool TryGetRandomPointAround(uint mapId, Vector3 center, float radius, Random random, PathOptions? options, out Vector3 point)
        {
            point = center;
            return available;
        }

        public MeshRaycastResult Raycast(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
            => new(available, hit, hit ? 0.5f : 1.0f, hit ? Vector3.Lerp(start, end, 0.5f) : end);
    }

    private sealed class InstantMover(int seed) : ICreatureMover
    {
        private readonly Random _rng = new(seed);
        private readonly CreatureMovementOptions _options = new();

        public CreatureMovementOptions MovementOptions => _options;

        public void MoveTo(Creature creature, float x, float y, float z, bool run, float? finalOrientation)
        {
            creature.Relocate(x, y, z, finalOrientation ?? creature.Orientation, creature.Movement.Time);
        }

        public void MovePath(Creature creature, IReadOnlyList<Vector3> path, bool run, SplineFacing facing)
        {
            if (path.Count > 0)
            {
                Vector3 end = path[^1];
                creature.Relocate(end.X, end.Y, end.Z, facing.FinalAngle ?? creature.Orientation, creature.Movement.Time);
            }
        }

        public IReadOnlyList<Vector3> FindPath(Creature creature, Vector3 destination) => [destination];

        public PlannedPath PlanPath(Creature creature, Vector3 destination, bool toSpawn = false)
            => new(PathOutcome.Direct, [destination]);

        public bool TryGetRandomMeshPoint(Creature creature, Vector3 center, float radius, out Vector3 point)
        {
            point = center;
            return false;
        }

        public void StopMoving(Creature creature) => creature.StopSpline(0L, creature.Movement.Time);

        public void SetFacingTo(Creature creature, float angle) => creature.Orientation = Creature.NormalizeOrientation(angle);

        public float? GetHeight(uint mapId, float x, float y, float z) => z;

        public bool IsCasting(Creature creature) => false;

        public void OnMovementFinished(Creature creature, MovementGeneratorType type, uint pointId) { }

        public int URand(int min, int max)
            => max <= min ? min : _rng.Next(min, max + 1);

        public double NextDouble() => _rng.NextDouble();
    }

    private sealed class DummyInstanceData(Map map) : InstanceData(map)
    {
        public override void Load(string state) { }
    }

    private sealed class NoOpScriptHook : IWorldHooks;

    private sealed class CountingWorldHook : IWorldHooks
    {
        public int Updates { get; private set; }
        public void OnUpdate(uint diffMs) => Updates++;
    }
}
