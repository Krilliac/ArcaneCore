using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_LEVELUP_INFO: VanillaServerMessage, IWorldMessage {
    public required uint NewLevel { get; set; }
    public required uint Health { get; set; }
    public required uint Mana { get; set; }
    public required uint Rage { get; set; }
    public required uint Focus { get; set; }
    public required uint Energy { get; set; }
    public required uint Happiness { get; set; }
    public required uint Strength { get; set; }
    public required uint Agility { get; set; }
    public required uint Stamina { get; set; }
    public required uint Intellect { get; set; }
    public required uint Spirit { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt(NewLevel, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Health, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Mana, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Rage, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Focus, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Energy, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Happiness, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Strength, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Agility, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Stamina, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Intellect, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Spirit, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, 50, 468, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, 50, 468, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_LEVELUP_INFO> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var newLevel = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var health = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var mana = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var rage = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var focus = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var energy = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var happiness = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var strength = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var agility = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var stamina = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var intellect = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var spirit = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new SMSG_LEVELUP_INFO {
            NewLevel = newLevel,
            Health = health,
            Mana = mana,
            Rage = rage,
            Focus = focus,
            Energy = energy,
            Happiness = happiness,
            Strength = strength,
            Agility = agility,
            Stamina = stamina,
            Intellect = intellect,
            Spirit = spirit,
        };
    }

}

