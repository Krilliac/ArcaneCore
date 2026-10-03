using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Protocol;
using ArcaneCore.World.Tests.Spells;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Tests.Duel;

/// <summary>A clock a test steps by hand (whole Unix seconds).</summary>
internal sealed class ManualTimeProvider(long unixSeconds) : TimeProvider
{
    private long _unix = unixSeconds;

    public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(Interlocked.Read(ref _unix));

    public void Advance(long seconds) => Interlocked.Add(ref _unix, seconds);
}

/// <summary>
/// A world host whose human warrior knows spell 7266 "Duel" (classic-db spell_template: Effect1 83, TargetA 25, EffectMiscValue 21680; range and duration are
/// synthetic) and whose game object content has the Duel Flag (gameobject_template 21680, type 16). The duel clock is <see cref="Clock"/>.
/// </summary>
internal static class DuelWorldHost
{
    public const uint DuelSpell = 7266;
    public const uint GrovelSpell = 7267;
    public const uint FlagEntry = 21680;

    public static ManualTimeProvider NewClock() => new(1_800_000_000);

    public static WorldTestHost Start(ManualTimeProvider clock, Action<IServiceCollection>? more = null)
        => WorldTestHost.Start(configureServices: services =>
        {
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton<ISpellContentStore>(new InMemorySpellContentStore(Content()));
            services.AddScoped<ArcaneCore.Kernel.WorldData.GameObjects.IGameObjectDataStore>(_ => new FlagStore());
            more?.Invoke(services);
        });

    public static SpellContent Content() => new(
        [
            new SpellTemplateRow
            {
                Id = DuelSpell,
                SpellName = "Duel",
                RangeIndex = 4,
                DurationIndex = 3,
                Effect1 = 83,
                EffectBaseDice1 = 1,
                EffectDieSides1 = 1,
                EffectImplicitTargetA1 = 25,
                EffectMiscValue1 = (int)FlagEntry,
            },
            new SpellTemplateRow
            {
                Id = GrovelSpell,
                SpellName = "Grovel",
                RangeIndex = 1,
                DurationIndex = 4,
                Effect1 = 6,
                EffectBaseDice1 = 1,
                EffectDieSides1 = 1,
                EffectImplicitTargetA1 = 1,
                EffectApplyAuraName1 = 12, // SPELL_AURA_MOD_STUN
                SpellVisual = 1,
            },
        ],
        [],
        [new SpellDurationRow { Id = 3, Duration = 600_000, MaxDuration = 600_000 }, new SpellDurationRow { Id = 4, Duration = 10_000, MaxDuration = 10_000 }],
        [new SpellRangeRow { Id = 1 }, new SpellRangeRow { Id = 4, MaxRange = 30 }],
        [],
        [new PlayerCreateSpellRow { Race = 1, Class = 1, Spell = DuelSpell }],
        []);

    public static byte[] CastPayload(uint spell, ulong unit)
    {
        var writer = new PacketWriter(16);
        writer.WriteUInt32(spell);
        SpellCastTargets.ForUnit(new ObjectGuid(unit)).Write(writer);
        return writer.ToArray();
    }

    private sealed class FlagStore : ArcaneCore.Kernel.WorldData.GameObjects.IGameObjectDataStore
    {
        public Task<GameObjectContent> LoadAsync(CancellationToken cancellationToken)
        {
            var flag = new GameObjectTemplate
            {
                Entry = FlagEntry,
                Type = (uint)ArcaneCore.Game.GameObjects.GameObjectType.DuelArbiter,
                DisplayId = 787,
                Name = "Duel Flag",
                Data = new uint[GameObjectTemplate.DataCount],
            };
            return Task.FromResult(new GameObjectContent([flag], [], [], [], []));
        }
    }
}
