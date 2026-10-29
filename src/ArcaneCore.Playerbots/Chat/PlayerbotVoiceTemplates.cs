namespace ArcaneCore.World.Playerbots.Chat;

/// <summary>
/// The built-in lines of the bot voice (<see cref="PlayerbotNeedsVoice"/>), merged into the chat templates (<see cref="PlayerbotBuiltinChat.Pick"/>
/// reads these as well). They are written the way players typed in 1.12 chat: short, mostly lower case, the slang of the time ("LFG",
/// "LFM", "WTS", "WTB", "inc", "oom", "rez", "ty", "gz", "brb", "EFC"). A phrasing is used only when every placeholder in it has a value;
/// the values come from the bot's real state, never invented. Besides the chat placeholders (<c>{name}</c>, <c>{level}</c>, <c>{class}</c>,
/// <c>{zone}</c>, ...) a voice topic fills <c>{dungeon}</c> (an instance, as players named it), <c>{roles}</c> ("tank and heals"),
/// <c>{count}</c>, <c>{task}</c> (a quest title), <c>{item}</c>, <c>{price}</c> ("1g 20s"), <c>{place}</c>, <c>{mob}</c>, <c>{boss}</c>,
/// <c>{who}</c>, <c>{newlevel}</c>, <c>{newzone}</c>, <c>{node}</c> (a battleground base or tower), <c>{where}</c> and <c>{minlevel}</c>.
/// Keys may have the chat's race, class and level variants (<c>voice.oom.class.Mage</c>).
/// </summary>
internal static class PlayerbotVoiceTemplates
{
    internal static readonly IReadOnlyDictionary<string, string[]> Lines = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        // --- looking for group ------------------------------------------------------------------------------------------------
        ["voice.lfg.dungeon"] =
        [
            "LFG {dungeon}, {level} {class}", "{level} {class} LFG {dungeon}", "LFG {dungeon}", "lfg {dungeon} {level} {class}",
            "{class} {level} looking for {dungeon} group", "any {dungeon} groups? {level} {class} lfg", "LF {dungeon} grp, {class} {level}",
            "{level} {class} lf {dungeon}, whisper me",
        ],
        ["voice.lfg.dungeon.class.Warrior"] = ["{level} warrior tank LFG {dungeon}", "LFG {dungeon}, can tank"],
        ["voice.lfg.dungeon.class.Priest"] = ["{level} priest LFG {dungeon}, can heal", "healer LFG {dungeon}"],
        ["voice.lfg.dungeon.class.Druid"] = ["{level} druid LFG {dungeon}, can heal or tank"],
        ["voice.lfg.dungeon.class.Shaman"] = ["{level} shaman LFG {dungeon}, can heal"],
        ["voice.lfg.dungeon.class.Paladin"] = ["{level} pally LFG {dungeon}, can heal"],
        ["voice.lfm.dungeon"] =
        [
            "LFM {dungeon} need {roles}", "LF{count}M {dungeon}, need {roles}", "LFM {dungeon}, {roles} pst", "{dungeon} LFM need {roles}",
            "need {roles} for {dungeon}", "LF{count}M {dungeon}", "LFM {dungeon} need {roles}, whisper me", "{dungeon} group LF {roles}",
        ],
        ["voice.lfm.dungeon.dps"] =
        [
            "LF{count}M {dungeon}, need dps", "LFM {dungeon} need {count} more", "{dungeon} LF{count}M, any class", "need {count} dps for {dungeon}",
            "LF{count}M {dungeon} pst",
        ],
        ["voice.lfg.quest"] =
        [
            "LFG {task}", "anyone for {task}?", "need help with {task}, anyone?", "LFM {task}", "{level} {class} lfg {task}",
            "anyone doing {task}? need a group", "lf group for {task}", "anyone want to do {task}?",
        ],
        ["voice.lfm.quest"] =
        [
            "LFM {task}, need {roles}", "LF{count}M {task}", "need {roles} for {task}", "{task} group LF {roles}", "LFM {task} pst",
        ],
        ["voice.lfm.quest.dps"] = ["LF{count}M {task}", "need {count} more for {task}", "LFM {task}, any class"],
        ["voice.questhelp"] =
        [
            "anyone for {task}?", "can anyone help with {task}?", "need help with {task}", "anyone up for {task}? cant solo it",
            "{task} anyone? its elite", "lf help {task}", "anyone else on {task}?",
        ],

        // --- bots answering a group call, and recruits joining a waiting group ------------------------------------------------------
        ["voice.recruit.tank"] = ["inv me, ill tank", "ill tank, inv", "can tank, inv me", "coming, ill tank", "inv, warrior tank here"],
        ["voice.recruit.heal"] = ["inv me, ill heal", "ill come, need a healer?", "can heal, inv me", "coming, ill heal", "need a healer? inv me"],
        ["voice.recruit.dps"] = ["inv me, ill come", "ill come, need dps?", "inv me pls", "can join, inv", "coming along"],

        // --- answers to whispers about a call ---------------------------------------------------------------------------------
        ["voice.reply.lfg.ready"] =
        [
            "sure, inv in a sec", "k, inv coming", "cool, sec", "yep, inv", "sure thing, sec", "k inv",
        ],
        ["voice.reply.lfg.noted"] =
        [
            "cool, need {count} more then i inv", "k, got you, still need {count}", "sure, will inv when we have {count} more",
            "added you, {count} more to go", "nice, need {count} more, will inv",
        ],
        ["voice.reply.lfg.level"] = ["sorry, need {minlevel}+ for this", "need lvl {minlevel} for it, sorry", "its {minlevel}+ sorry"],
        ["voice.reply.lfg.grouped"] = ["leave your group and whisper me again", "you're in a group, drop it and pst", "need you out of your group first"],
        ["voice.reply.lfg.gone"] = ["sorry, not looking anymore", "nvm, gave up on it", "sorry, got it done already", "not anymore, sorry"],
        ["voice.reply.wts"] =
        [
            "{item} for {price}, im in {place}", "{price} for {item}, come to {place}", "{item} is {price}, meet me in {place}",
            "{price}, im at {place}", "still have it, {price}, in {place}", "{price} for the {item}, in {place} now",
        ],
        ["voice.reply.wts.gone"] = ["sorry, sold it", "gone, sorry", "dont have it anymore, sorry"],
        ["voice.reply.wtb"] =
        [
            "ill pay {price} for {count} {item}", "{price} for {count} {item}, deal?", "can pay {price} for {count} {item}", "{count} {item} for {price}, ok?",
            "i pay {price} for {count} {item}",
        ],

        // --- a quoted deal: the desk (PlayerbotTradeDesk) agreed, held its price, or could not take the deal -------------------------------
        ["voice.deal.agreed"] =
        [
            "deal, {price} for {item}, im in {place}, open trade", "ok, {price} for {item}. open trade, im in {place}", "deal! {price}, open trade with me in {place}",
            "sure, {price} for {item}, trade me in {place}", "k deal, {price}, im at {place}, open trade",
        ],
        ["voice.deal.counter.no"] = ["sorry, {price} is my price", "sorry, cant go lower than {price}", "{price} is what i can do, sorry", "sorry, {price} or no deal"],
        ["voice.deal.refused"] = ["sorry, cant do that deal right now", "sorry, not right now", "cant right now, sorry", "sorry, cant trade right now"],
        ["voice.deal.off"] = ["deal is off, sorry", "sorry, deal is off", "deal off, sorry", "cant do it anymore, sorry"],

        // --- trade --------------------------------------------------------------------------------------------------------------
        ["voice.wts"] =
        [
            "WTS {item} {price}", "WTS {item}, {price}", "WTS {item} pst", "selling {item} {price}", "WTS {item}, {price} or offer",
            "anyone need {item}? {price}", "WTS {item} {price} whisper me", "{item} for sale, {price}",
        ],
        ["voice.wts.stack"] =
        [
            "WTS {item} x{count}, {price} each", "WTS {count} {item} {price} ea", "selling {item} x{count} {price} each",
            "WTS {item} x{count} pst", "{count} {item} for sale, {price} ea",
        ],
        ["voice.wtb"] =
        [
            "WTB {item} x{count}", "WTB {count} {item}", "WTB {item} pst", "buying {item}, need {count}", "anyone selling {item}? need {count}",
            "WTB {item}, need {count} for a recipe", "LF {item} x{count}",
        ],
        ["voice.water.channel"] = ["any mage selling water?", "WTB water", "anyone have water to spare?", "lf mage for water"],
        ["voice.water.party"] = ["{who} water pls?", "{who} can i get some water?", "water pls {who}", "{who}, could use some water"],

        // --- party and raid needs ---------------------------------------------------------------------------------------------
        ["voice.oom"] = ["oom", "OOM", "oom!", "im oom", "out of mana", "low mana", "oom, careful"],
        ["voice.oom.class.Priest"] = ["oom, cant heal", "healer oom!"],
        ["voice.oom.class.Mage"] = ["oom, need to drink after this"],
        ["voice.heals"] = ["need heals", "heal pls", "heals!", "need heal!", "help, low hp", "heal me pls"],
        ["voice.rez"] = ["rez pls", "can i get a rez?", "rez?", "rez plz", "need a rez", "someone rez me pls"],
        ["voice.inc"] = ["inc {count}", "{count} inc", "pulling {count}", "inc {count}, get ready", "{count} coming"],
        ["voice.aggro"] = ["{mob} on me", "{mob} on me!", "help, {mob} on me", "got {mob} on me", "{mob} attacking me"],
        ["voice.brb.drink"] = ["drinking", "brb drinking", "sec, need to drink", "drink break", "hold on, drinking"],
        ["voice.brb.eat"] = ["sec, eating", "brb eating", "food break", "need to eat, sec"],
        ["voice.brb.vendor"] = ["brb vendor", "brb, bags full", "need to vendor, brb", "brb repair"],
        ["voice.brb.train"] = ["brb training", "brb, need to train", "going to train, back soon"],
        ["voice.ty"] = ["ty", "ty {who}", "thanks {who}", "thx", "ty for the buff", "thanks for the buff {who}"],
        ["voice.ty.rez"] = ["ty", "ty {who}", "thanks for the rez", "ty for rez {who}", "thx {who}"],
        ["voice.ding.party"] = ["ding", "ding!", "ding {newlevel}", "ding! {newlevel}", "{newlevel} finally", "woot ding"],
        ["voice.gz"] = ["gz", "grats", "gz {who}", "grats {who}", "gratz", "nice, gz"],
        ["voice.share"] = ["can someone share {task}?", "share {task} pls", "anyone share {task}?", "pls share {task}, dont have it"],
        ["voice.newzone"] =
        [
            "first time in {newzone}", "never been to {newzone} before", "so this is {newzone}", "first time here",
            "wow, first time in {newzone}",
        ],

        // --- the plan of a run's leader ---------------------------------------------------------------------------------------
        ["voice.run.pull"] = ["pulling {boss}", "{boss} next, pulling", "ready? pulling {boss}", "{boss}, here we go", "pulling {boss}, heals ready"],
        ["voice.run.mana"] = ["wait for mana", "drink up, wait for mana", "hold, mana break", "waiting for mana", "everyone drink"],
        ["voice.run.wipe"] = ["wipe, run back", "wipe. run back and rebuff", "well, wipe. run back", "wiped, run back in", "run back, we try again"],
        ["voice.run.bossdown"] = ["{boss} down", "{boss} down, nice", "gj, {boss} dead", "{boss} dead, moving on"],
        ["voice.run.enter"] = ["everyone in?", "zoning in", "ok, going in", "all in, lets go"],
        ["voice.run.done"] = ["gg, ty all", "gg", "good run, ty", "nice run all", "gg everyone, thanks"],

        // --- battleground callouts ---------------------------------------------------------------------------------------------
        ["voice.bg.flagtaken"] = ["our flag taken!", "flag taken!", "they got our flag", "efc! flag taken", "flag gone, get it back"],
        ["voice.bg.efc"] = ["EFC {where}", "efc {where}!", "flag carrier {where}", "EFC {where}, get him", "efc going {where}"],
        ["voice.bg.flagback"] = ["flag returned", "our flag is back", "flag back"],
        ["voice.bg.inc"] = ["inc {node}", "INC {node}", "inc {node}!", "{node} inc", "{node} under attack"],
        ["voice.bg.help"] = ["need help at {node}", "help {node}!", "{node} needs help", "def {node}"],
        ["voice.bg.ours"] = ["{node} is ours", "got {node}", "{node} capped", "{node} ours", "took {node}"],
        ["voice.bg.lost"] = ["lost {node}", "{node} lost", "they took {node}", "{node} gone"],
        ["voice.bg.capping"] = ["capping {node}", "assaulting {node}", "taking {node}", "on {node}, cover me"],
        ["voice.av.attacked"] = ["{node} under attack!", "inc {node}", "def {node}!", "they're on {node}"],
        ["voice.av.down"] = ["{node} down", "lost {node}", "{node} gone", "they burned {node}"],
        ["voice.av.burned"] = ["{node} burned", "{node} down, nice", "burned {node}", "{node} is down"],
        ["voice.av.assaulted"] = ["{node} assaulted", "capping {node}", "{node} ours soon, hold it", "took the flag at {node}"],
        ["voice.av.taken"] = ["{node} ours", "got {node}", "{node} is ours"],
    };
}
