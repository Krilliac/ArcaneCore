using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Reload;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Chat;
using ArcaneCore.World.Reload;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Chat;

/// <summary>
/// <c>.reload config</c> of <c>World:Playerbots:Chat:Safety</c>: every key is live and changed in place (the chat service reads the
/// same object), an out-of-range value is refused and changes nothing, and a removed key returns to its default. No Database section.
/// </summary>
public sealed class PlayerbotChatSafetyReloadTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arcane-safety-reload-" + Guid.NewGuid().ToString("N"));
    private readonly WorldRuntime _world;

    public PlayerbotChatSafetyReloadTests()
    {
        Directory.CreateDirectory(_directory);
        _world = new WorldRuntime(new WorldRuntimeOptions { TickIntervalMs = 5, AutosaveIntervalMs = 0 }, new NullSaveQueue(), NullLogger<WorldRuntime>.Instance);
    }

    public void Dispose()
    {
        _world.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private string Write(string json)
    {
        string path = Path.Combine(_directory, "appsettings.json");
        File.WriteAllText(path, json);
        return path;
    }

    private ReloadCoordinator Reloader(string path, PlayerbotOptions playerbots)
    {
        var configuration = new ConfigurationManager();
        configuration.AddJsonFile(path, optional: false, reloadOnChange: false);
        IServiceCollection services = new ServiceCollection().AddSingleton<IConfiguration>(configuration).AddSingleton(Options.Create(playerbots));
        var coordinator = new ReloadCoordinator(NullLogger.Instance);
        coordinator.Attach(_world);
        coordinator.Register(new ConfigContentReloadable(services.BuildServiceProvider()));
        _world.Start();
        return coordinator;
    }

    [Fact]
    public async Task EverySafetyKey_IsLive_InPlace_AndRangeChecked()
    {
        string path = Write("""
            { "World": { "AutosaveIntervalMs": 0, "TickIntervalMs": 5, "Playerbots": { "Enabled": true, "Chat": { "Safety": {
                "Enabled": false, "Categories": "Hate, Threats", "TermsFile": "ops/terms.json", "ReplaceDefaultTerms": true,
                "OnFlagged": "Ignore", "ScreenOutput": false, "OnOutputFlagged": "Drop", "StrikesBeforeCutoff": 5, "StrikeWindowMinutes": 120,
                "CutoffMinutes": 240, "AutoMute": true, "AutoMuteMinutes": 45, "FlagLogSize": 50, "StoreExcerpt": false, "Disclosure": false,
                "DisclosureText": "Bots here may answer with AI.", "AllowOptOut": false, "RequireOptIn": true, "StripPersonalData": false } } } } }
            """);
        var playerbots = new PlayerbotOptions { Enabled = true };
        PlayerbotChatSafetyOptions safety = playerbots.Chat.Safety;
        ReloadCoordinator coordinator = Reloader(path, playerbots);

        Assert.Equal(ReloadStatus.Applied, (await coordinator.ReloadAsync("config")).Status);
        Assert.Same(safety, playerbots.Chat.Safety);
        Assert.False(safety.Enabled);
        Assert.Equal(PlayerbotChatSafetyCategories.Hate | PlayerbotChatSafetyCategories.Threats, safety.Categories);
        Assert.Equal(("ops/terms.json", true), (safety.TermsFile, safety.ReplaceDefaultTerms));
        Assert.Equal((PlayerbotChatFlaggedAction.Ignore, false, PlayerbotChatOutputAction.Drop), (safety.OnFlagged, safety.ScreenOutput, safety.OnOutputFlagged));
        Assert.Equal((5, 120, 240), (safety.StrikesBeforeCutoff, safety.StrikeWindowMinutes, safety.CutoffMinutes));
        Assert.Equal((true, 45, 50, false), (safety.AutoMute, safety.AutoMuteMinutes, safety.FlagLogSize, safety.StoreExcerpt));
        Assert.Equal((false, "Bots here may answer with AI.", false, true, false),
            (safety.Disclosure, safety.DisclosureText, safety.AllowOptOut, safety.RequireOptIn, safety.StripPersonalData));
        Assert.Contains("no changes", (await coordinator.ReloadAsync("config")).Message);

        // Out of range: refused, nothing changes.
        foreach (string bad in new[] { "\"StrikesBeforeCutoff\": 0", "\"CutoffMinutes\": 20000", "\"FlagLogSize\": -1", "\"DisclosureText\": \"a|b\"", "\"AutoMuteMinutes\": 0" })
        {
            File.WriteAllText(path, $$"""{ "World": { "AutosaveIntervalMs": 0, "TickIntervalMs": 5, "Playerbots": { "Chat": { "Safety": { {{bad}} } } } } }""");
            Assert.NotEqual(ReloadStatus.Applied, (await coordinator.ReloadAsync("config")).Status);
            Assert.Equal((5, 240, 50, 45), (safety.StrikesBeforeCutoff, safety.CutoffMinutes, safety.FlagLogSize, safety.AutoMuteMinutes));
        }

        // Removed: the defaults come back.
        File.WriteAllText(path, """{ "World": { "AutosaveIntervalMs": 0, "TickIntervalMs": 5 } }""");
        Assert.Equal(ReloadStatus.Applied, (await coordinator.ReloadAsync("config")).Status);
        Assert.True(safety.Enabled && safety.ScreenOutput && safety.Disclosure && safety.AllowOptOut && safety.StripPersonalData);
        Assert.False(safety.RequireOptIn || safety.AutoMute);
        Assert.Equal(PlayerbotChatSafetyCategories.All, safety.Categories);
        Assert.Equal((3, 60, 60, 200), (safety.StrikesBeforeCutoff, safety.StrikeWindowMinutes, safety.CutoffMinutes, safety.FlagLogSize));
        Assert.Equal(string.Empty, safety.TermsFile);
    }

    [Fact]
    public void EverySafetyOption_HasALiveReloadKey()
    {
        string[] keys = [.. WorldConfigKeys.All.Where(key => key.Path.StartsWith("World:Playerbots:Chat:Safety:", StringComparison.Ordinal)).Select(key => key.Path)];
        string[] properties = [.. typeof(PlayerbotChatSafetyOptions).GetProperties().Select(p => "World:Playerbots:Chat:Safety:" + p.Name)];
        Assert.Equal(properties.Order(), keys.Order());
        Assert.All(WorldConfigKeys.All.Where(key => keys.Contains(key.Path)), key => Assert.True(key.Live, key.Path));
    }

    private sealed class NullSaveQueue : ICharacterSaveQueue
    {
        public void Enqueue(CharacterState state)
        {
        }
    }
}
