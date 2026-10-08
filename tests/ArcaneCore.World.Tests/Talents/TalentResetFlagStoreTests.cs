using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Rename;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Talents;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.World.Tests.Talents;

/// <summary>
/// <see cref="EfTalentResetFlagStore"/> on SQLite: the request is the AT_LOGIN_RESET_TALENTS bit of <c>character_at_login</c>, so it
/// shares the row with the rename request without disturbing it.
/// </summary>
public sealed class TalentResetFlagStoreTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "arcane-talent-flags-" + Guid.NewGuid().ToString("N") + ".db");
    private DbContextOptions<CharacterDbContext> _options = null!;

    public async Task InitializeAsync()
    {
        _options = new DbContextOptionsBuilder<CharacterDbContext>().UseSqlite($"Data Source={_path};Pooling=False").Options;
        await using CharacterDbContext db = new(_options);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
    }

    public Task DisposeAsync()
    {
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
            // A closing handle; the temp file is harmless.
        }

        return Task.CompletedTask;
    }

    private async Task<int> CreateAsync(string name)
    {
        await using CharacterDbContext db = new(_options);
        return (await new EfCharacterStore(db).CreateAsync(new CharacterRecord { AccountId = 1, Name = name, Race = 1, Class = 1, Level = 12 })).Id;
    }

    private async Task<uint> AtLoginAsync(int characterId)
    {
        await using CharacterDbContext db = new(_options);
        return await db.Set<CharacterAtLoginRow>().Where(r => r.CharacterId == characterId).Select(r => r.Flags).FirstOrDefaultAsync();
    }

    [Fact]
    public async Task FlagReadAndClear_RoundTrip_AndLeaveTheRenameBitAlone()
    {
        int id = await CreateAsync("Flagme");
        await using (CharacterDbContext db = new(_options))
        {
            Assert.True(await new EfCharacterRenameStore(db).SetFlagAsync(id, CharacterAtLoginFlags.Rename));
        }

        await using (CharacterDbContext db = new(_options))
        {
            var store = new EfTalentResetFlagStore(db);
            Assert.False(await store.IsFlaggedAsync(id));
            Assert.True(await store.FlagAsync(id));
            Assert.True(await store.FlagAsync(id));   // idempotent
            Assert.True(await store.IsFlaggedAsync(id));
            Assert.False(await store.FlagAsync(id + 100));   // no such character
        }

        Assert.Equal(CharacterAtLoginFlags.Rename | CharacterAtLoginFlags.ResetTalents, await AtLoginAsync(id));

        await using (CharacterDbContext db = new(_options))
        {
            var store = new EfTalentResetFlagStore(db);
            await store.ClearAsync(id);
            await store.ClearAsync(id);   // no-op
            Assert.False(await store.IsFlaggedAsync(id));
        }

        Assert.Equal(CharacterAtLoginFlags.Rename, await AtLoginAsync(id));
    }

    [Fact]
    public async Task FlagAll_FlagsEveryCharacter_WithOrWithoutARow_AndCountsTheNewOnes()
    {
        int noRow = await CreateAsync("Norow");
        int renameRow = await CreateAsync("Renamerow");
        int flagged = await CreateAsync("Flagged");
        await using (CharacterDbContext db = new(_options))
        {
            await new EfCharacterRenameStore(db).SetFlagAsync(renameRow, CharacterAtLoginFlags.Rename);
            await new EfTalentResetFlagStore(db).FlagAsync(flagged);
        }

        await using (CharacterDbContext db = new(_options))
        {
            Assert.Equal(2, await new EfTalentResetFlagStore(db).FlagAllAsync());
        }

        Assert.Equal(CharacterAtLoginFlags.ResetTalents, await AtLoginAsync(noRow));
        Assert.Equal(CharacterAtLoginFlags.Rename | CharacterAtLoginFlags.ResetTalents, await AtLoginAsync(renameRow));
        Assert.Equal(CharacterAtLoginFlags.ResetTalents, await AtLoginAsync(flagged));
    }

    [Fact]
    public async Task TheCharacterListShowsNoRenamePrompt_ForAResetRequest()
    {
        int id = await CreateAsync("Norename");
        await using CharacterDbContext db = new(_options);
        await new EfTalentResetFlagStore(db).FlagAsync(id);

        IReadOnlyDictionary<int, uint> flags = await new EfCharacterRenameStore(db).GetFlagsAsync(1);

        Assert.Equal(0u, ArcaneCore.World.Characters.Rename.CharacterRename.CharEnumFlags(flags[id]));
    }
}
