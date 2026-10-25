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
        ["help.sorry"] = ["Sorry, can't right now.", "Sorry, I can't help just now.", "I can't, sorry!", "Sorry, I'm not able to."],
        ["social.thanks"] = ["Thanks, {player}!", "Thank you, {player}.", "Much obliged, {player}!", "Thanks for the help, {player}.", "I owe you one, {player}!"],
        ["social.team"] = ["You on the {objective} too? Want to team up?", "Are you after the {objective} as well? Shall we team up?", "{objective}, too? Want to group up?"],
        ["social.followup"] = ["Did you ever sort out {topic}?", "How did {topic} go, {player}?", "So, what happened with {topic}?"],
        ["social.arrive"] = ["Welcome back, {player}!", "Good to see you again, {player}.", "Hey {player}, long time no see!"],
        ["comment.rare"] = ["Look, a rare one is around here.", "Careful, there is a rare creature near us.", "Is that a rare spawn over there?"],
        ["comment.event"] = ["Looks like the {event} has started!", "The {event} is on. Something is going on in the world."],
        ["event.midsummer.arrive"] = ["Happy Midsummer, everyone!", "The bonfires are lit. Come and dance!", "Midsummer at last. Nothing like it."],
        ["event.winterveil.arrive"] = ["Happy Winter Veil, everyone!", "Best part of winter, this.", "Warm drinks and presents, what more could you want?"],
        ["event.lunar.arrive"] = ["Happy Lunar Festival, everyone!", "Honor to the elders. Happy Lunar Festival!", "Fireworks and family, the Lunar Festival is here."],
        ["event.love.arrive"] = ["Love is in the air, everyone!", "Happy Love is in the Air!", "Spread a little love around, friends."],
        ["event.noblegarden.arrive"] = ["Happy Noblegarden, everyone!", "Seen any eggs about? It is Noblegarden!", "Spring is here and the eggs are out."],
        ["event.children.arrive"] = ["Happy Children's Week, everyone!", "Children's Week is on. Be kind to the little ones.", "Always lovely to see the orphans out and about."],
        ["event.hallows.arrive"] = ["Happy Hallow's End, everyone!", "Trick or treat! Hallow's End is here.", "Watch your candy buckets, it is Hallow's End."],
        ["event.harvest.arrive"] = ["Happy Harvest Festival, everyone!", "A good harvest to you all.", "Plenty on the tables this Harvest Festival."],
        ["event.darkmoon.arrive"] = ["The Darkmoon Faire is in town, everyone!", "Step right up, the Darkmoon Faire is open!", "Tickets, prizes and fortunes: the Faire is here."],
        ["event.fishing.arrive"] = ["Good luck in the fishing contest, everyone!", "May the biggest catch win.", "Lines in the water, friends. The contest is on."],
        ["event.festival.arrive"] = ["Happy festival, everyone!", "A fine day for a celebration.", "Good to see the festivities in full swing."],
        ["comment.raid"] = ["That is a lot of enemy players around.", "Enemy raid in the zone. Stay sharp.", "Plenty of the other faction about, watch out."],
        ["comment.mount"] = ["Nice, you got a mount, {player}!", "A mount at last, {player}. Grats!", "Grats on the mount, {player}!"],
        ["social.thanks.mood.cheerful"] = ["Thank you so much, {player}!", "You are a star, {player}. Thanks!", "Thanks a million, {player}!"],
        ["social.thanks.mood.grumpy"] = ["Hmph. Thanks, {player}.", "Fine. Thanks.", "Thanks, I suppose, {player}."],
        ["help.sorry.mood.grumpy"] = ["Not now.", "Can't. Sorry.", "No time, sorry."],
        ["social.greet.mood.cheerful"] = ["What a day! Hello, {player}!", "Hi {player}, lovely to see you!", "Well met, {player}! Great day!"],
        ["social.greet.mood.grumpy"] = ["Hmm. {player}.", "Oh. Hello, {player}.", "Yes? Hello, {player}."],
        ["social.greet"] = ["Well met, {player}.", "Hello there, {player}!", "Greetings, {player}.", "Hi {player}, good to see you."],
    };
}
