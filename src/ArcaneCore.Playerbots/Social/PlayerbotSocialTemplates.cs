namespace ArcaneCore.World.Playerbots.BotSocial;

/// <summary>
/// The built-in lines of the social features, merged into the bot chat templates (<see cref="Chat.PlayerbotBuiltinChat.Pick"/> reads
/// these as well as <see cref="Chat.PlayerbotChatTemplates.Lines"/>). A phrasing is used only when every placeholder in it has a value.
/// Besides the chat placeholders (<c>{name}</c>, <c>{level}</c>, <c>{race}</c>, <c>{class}</c>, <c>{zone}</c>, <c>{goal}</c>, ...) a guild
/// topic fills <c>{guild}</c>, <c>{who}</c> (the member the line is about), <c>{newlevel}</c>, <c>{boss}</c>, <c>{instance}</c> and
/// <c>{battleground}</c>. Lines state only what the bot knows of itself and what just happened: no invented lore.
/// <para>
/// Keys: <c>guild.welcome</c> (a member joined), <c>guild.grats</c> (another member reached a milestone level), <c>guild.ding</c> (the
/// speaker reached one), <c>guild.boss</c> (a dungeon or raid boss fell), <c>guild.lfm</c> (a bot's dungeon group is forming: "anyone for {dungeon}?"), <c>guild.bgwin</c> (a member won a battleground),
/// <c>guild.chatter</c> (small talk), <c>guild.motd</c> (a new guild's message of the day, at most 128 characters) and
/// <c>social.greet</c> (/say to a real player standing close by, <c>{player}</c> is the player).
/// </para>
/// </summary>
internal static class PlayerbotSocialTemplates
{
    internal static readonly IReadOnlyDictionary<string, string[]> Lines = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["guild.welcome"] =
        [
            "Welcome to {guild}, {who}!", "Hey {who}, welcome aboard.", "Glad to have you with us, {who}.", "Welcome, {who}. Make yourself at home.",
        ],
        ["guild.grats"] =
        [
            "Grats on level {newlevel}, {who}!", "Nice one {who}, level {newlevel}!", "Well done, {who}. Level {newlevel}!", "Level {newlevel}, {who}. Congratulations!",
        ],
        ["guild.ding"] = ["Ding! Level {newlevel}.", "Just hit level {newlevel}!", "Level {newlevel} at last.", "Ding, {newlevel}!"],
        ["guild.boss"] =
        [
            "{boss} is down in {instance}!", "Heard {boss} fell in {instance}. Nice work!", "{boss} is dead. Good run in {instance}.", "Well done on {boss}!",
        ],
        ["guild.lfm"] = ["anyone for {dungeon}?", "anyone up for {dungeon}?", "LF{dungeon} group, anyone?", "{dungeon} anyone? need a few more", "who wants to do {dungeon}?"],
        ["guild.bgwin"] = ["We won the battleground!", "A win in {battleground}. Good game, everyone.", "Victory in {battleground}!", "That was a good fight. We won."],
        ["guild.chatter"] =
        [
            "Quiet in here today.", "Anyone around?", "Level {level} {class} here, {goal}.", "Busy day in {zone}.", "How is everyone doing?",
            "{Goal}, the usual.", "Hello, {guild}.",
        ],
        ["guild.motd"] =
        [
            "Welcome to {guild}. Be kind, help each other and have fun.", "{guild}: friendly adventurers wanted. Say hello!",
            "Welcome, friend. {guild} looks after its own.",
        ],
        ["social.greet"] = ["Well met, {player}.", "Hello there, {player}!", "Greetings, {player}.", "Hi {player}, good to see you."],
    };
}
