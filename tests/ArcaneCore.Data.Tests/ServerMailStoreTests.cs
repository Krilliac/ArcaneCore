using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.ServerMail;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.ServerMail;
using Xunit;

namespace ArcaneCore.Data.Tests;

public sealed class ServerMailStoreTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Templates_load_and_sent_letters_are_recorded_once(DatabaseProvider provider)
    {
        var connection = await _databases.CreateAsync(provider);
        await using (CharacterDbContext setup = TestContexts.Create<CharacterDbContext>(connection))
        {
            await SchemaBootstrapper.EnsureAsync(setup, CharacterDbContext.Schema);
            setup.Add(new ServerMailTemplateEntity { Id = 1, SenderEntry = 0, MoneyA = 100, MoneyH = 200, Subject = "Welcome", Body = "Hi", Active = true });
            setup.Add(new ServerMailItemEntity { TemplateId = 1, Faction = "Horde", Item = 2589, ItemCount = 5 });
            setup.Add(new ServerMailConditionEntity { TemplateId = 1, ConditionType = "Level", ConditionValue = 10 });
            await setup.SaveChangesAsync();
        }

        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        var store = new EfServerMailStore(db);
        ServerMailContent content = await store.LoadAsync();
        Assert.Equal(new ServerMailTemplateRow(1, 0, 100, 200, "Welcome", "Hi", true), Assert.Single(content.Templates));
        Assert.Equal(new ServerMailItemRow(1, "Horde", 2589, 5), Assert.Single(content.Items));
        Assert.Equal(new ServerMailConditionRow(1, "Level", 10, 0), Assert.Single(content.Conditions));

        Assert.Empty(await store.GetSentAsync(7));
        await store.MarkSentAsync(7, 1);
        await store.MarkSentAsync(7, 1);
        Assert.Equal([1u], await store.GetSentAsync(7));
        Assert.Empty(await store.GetSentAsync(8));
    }
}
