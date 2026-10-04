using System.Collections.Concurrent;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Quests;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Tests.Npc;

internal sealed class QuestNpcTestServices : IWorldTestServices
{
    public static readonly AsyncLocal<QuestJournalFixture?> Current = new();

    public void Register(IServiceCollection services)
    {
        if (Current.Value is not { } fixture)
        {
            return;
        }

        services.AddSingleton<IQuestContentStore>(fixture);
        services.AddSingleton<INpcContentStore>(fixture);
        services.AddSingleton<ICharacterQuestStore>(fixture.Characters);
        services.AddSingleton<TimeProvider>(fixture.Clock);
    }
}

internal sealed class QuestJournalFixture : IQuestContentStore, INpcContentStore
{
    public const uint Ordinary = 9201;
    public const uint Timed = 9202;
    public const uint TextId = 9203;

    public ManualQuestClock Clock { get; } = new();
    public MemoryQuestStore Characters { get; } = new();

    /// <summary>Replaces the default two journal quests when set.</summary>
    public IReadOnlyList<QuestTemplate>? Templates { get; init; }

    /// <summary>The imported areatrigger_involvedrelation rows (<see cref="CreatureQuestRelation.Id"/> is the trigger).</summary>
    public IReadOnlyList<CreatureQuestRelation> AreaTriggerQuests { get; init; } = [];

    public Task<QuestContent> LoadAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Content() with { AreaTriggerQuests = AreaTriggerQuests });

    private QuestContent Content()
        => Templates is { } custom ? new QuestContent([.. custom], [], []) : new QuestContent(
        [
            new QuestTemplate
            {
                Entry = Ordinary, Method = 2, QuestLevel = 7, MinLevel = 1,
                Title = "Saved journal", Details = "Remember the task.", Objectives = "Do the work.",
                ReqCreatureOrGOId1 = 100, ReqCreatureOrGOCount1 = 5,
            },
            new QuestTemplate
            {
                Entry = Timed, Method = 2, QuestLevel = 1, MinLevel = 1,
                Title = "Timed journal", LimitTime = 30,
            },
        ], [], []);

    Task<NpcContent> INpcContentStore.LoadAsync(CancellationToken cancellationToken)
        => Task.FromResult(NpcContent.Empty with
        {
            NpcTexts = [new NpcText
            {
                Id = TextId,
                Options = [new NpcTextOption(1, "Hello, journal keeper.", "", 0, 0, 0, 0, 0, 0, 0)],
            }],
        });

    public CharacterQuestStatus Progress(int characterId, uint quest, long deadline = 0, uint count = 2)
        => new(characterId, quest, (byte)QuestStatus.Incomplete, false, false, deadline,
            count, 0, 0, 0, 0, 0, 0, 0, 0);
}

internal sealed class ManualQuestClock : TimeProvider
{
    private long _ticks = DateTimeOffset.UtcNow.Ticks;
    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
}

internal sealed class MemoryQuestStore : ICharacterQuestStore
{
    private readonly ConcurrentDictionary<(int Character, uint Quest), CharacterQuestStatus> _quests = new();
    private readonly ConcurrentDictionary<int, uint[]> _masks = new();
    private int _saveCalls;
    public volatile bool FailRead;
    public volatile bool FailWrite;
    public TaskCompletionSource? WriteEntered { get; set; }
    public TaskCompletionSource? AllowWrite { get; set; }
    public int SaveCalls => Volatile.Read(ref _saveCalls);

    public void Seed(params CharacterQuestStatus[] rows)
    {
        foreach (CharacterQuestStatus row in rows)
        {
            _quests[(row.CharacterId, row.Quest)] = row;
        }
    }

    public CharacterQuestStatus Stored(int characterId, uint quest) => _quests[(characterId, quest)];

    public Task<CharacterQuestData> LoadAsync(int characterId, CancellationToken cancellationToken = default)
    {
        if (FailRead)
        {
            throw new IOException("Quest database read unavailable");
        }

        return Task.FromResult(new CharacterQuestData(
            [.. _quests.Where(r => r.Key.Character == characterId).Select(r => r.Value).OrderBy(r => r.Quest)],
            _masks.TryGetValue(characterId, out uint[]? mask) ? mask.ToArray() : []));
    }

    public async Task SaveQuestsAsync(int characterId, IReadOnlyList<CharacterQuestStatus> upserts,
        CancellationToken cancellationToken = default)
    {
        WriteEntered?.TrySetResult();
        if (AllowWrite is { } release)
        {
            await release.Task.WaitAsync(cancellationToken);
        }

        if (FailWrite)
        {
            throw new IOException("Quest database write unavailable");
        }

        Seed(upserts.Select(r => r with { CharacterId = characterId }).ToArray());
        Interlocked.Increment(ref _saveCalls);
    }

    public Task SaveTaxiMaskAsync(int characterId, IReadOnlyList<uint> mask, CancellationToken cancellationToken = default)
    {
        if (FailWrite)
        {
            throw new IOException("Quest database write unavailable");
        }

        _masks[characterId] = mask.ToArray();
        return Task.CompletedTask;
    }
}
