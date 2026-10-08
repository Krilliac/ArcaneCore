using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_PET_SPELLS: VanillaServerMessage, IWorldMessage {
    public required ulong Pet { get; set; }
    public struct OptionalActionBars {
        public required uint Duration { get; set; }
        public required Vanilla.PetReactState React { get; set; }
        public required Vanilla.PetCommandState Command { get; set; }
        /// <summary>
        /// mangoszero: set to 0
        /// </summary>
        public required byte Unknown { get; set; }
        public required Vanilla.PetEnabled PetEnabled { get; set; }
        public const int ActionBarsLength = 10;
        public required uint[] ActionBars { get; set; }
        public required List<uint> Spells { get; set; }
        public required List<PetSpellCooldown> Cooldowns { get; set; }
    }
    public required OptionalActionBars? ActionBars { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteULong(Pet, cancellationToken).ConfigureAwait(false);

        if (ActionBars is { } actionBars) {
            await w.WriteUInt(actionBars.Duration, cancellationToken).ConfigureAwait(false);

            await w.WriteByte((byte)actionBars.React, cancellationToken).ConfigureAwait(false);

            await w.WriteByte((byte)actionBars.Command, cancellationToken).ConfigureAwait(false);

            await w.WriteByte(actionBars.Unknown, cancellationToken).ConfigureAwait(false);

            await w.WriteByte((byte)actionBars.PetEnabled, cancellationToken).ConfigureAwait(false);

            foreach (var v in actionBars.ActionBars) {
                await w.WriteUInt(v, cancellationToken).ConfigureAwait(false);
            }

            await w.WriteByte((byte)actionBars.Spells.Count, cancellationToken).ConfigureAwait(false);

            foreach (var v in actionBars.Spells) {
                await w.WriteUInt(v, cancellationToken).ConfigureAwait(false);
            }

            await w.WriteByte((byte)actionBars.Cooldowns.Count, cancellationToken).ConfigureAwait(false);

            foreach (var v in actionBars.Cooldowns) {
                await v.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
            }

        }
    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 377, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 377, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_PET_SPELLS> ReadBodyAsync(Stream r, uint bodySize, CancellationToken cancellationToken = default) {
        // ReSharper disable once InconsistentNaming
        var __size = 0;
        var pet = await r.ReadULong(cancellationToken).ConfigureAwait(false);
        __size += 8;

        OptionalActionBars? optionalActionBars = null;
        if (__size < bodySize) {
            var duration = await r.ReadUInt(cancellationToken).ConfigureAwait(false);
            __size += 4;

            var react = (Vanilla.PetReactState)await r.ReadByte(cancellationToken).ConfigureAwait(false);
            __size += 1;

            var command = (Vanilla.PetCommandState)await r.ReadByte(cancellationToken).ConfigureAwait(false);
            __size += 1;

            var unknown = await r.ReadByte(cancellationToken).ConfigureAwait(false);
            __size += 1;

            var petEnabled = (Vanilla.PetEnabled)await r.ReadByte(cancellationToken).ConfigureAwait(false);
            __size += 1;

            var actionBars = new uint[OptionalActionBars.ActionBarsLength];
            for (var i = 0; i < OptionalActionBars.ActionBarsLength; ++i) {
                actionBars[i] = await r.ReadUInt(cancellationToken).ConfigureAwait(false);
                __size += 4;
            }

            // ReSharper disable once UnusedVariable.Compiler
            var amountOfSpells = await r.ReadByte(cancellationToken).ConfigureAwait(false);
            __size += 1;

            var spells = new List<uint>();
            for (var i = 0; i < amountOfSpells; ++i) {
                spells.Add(await r.ReadUInt(cancellationToken).ConfigureAwait(false));
                __size += 4;
            }

            // ReSharper disable once UnusedVariable.Compiler
            var amountOfCooldowns = await r.ReadByte(cancellationToken).ConfigureAwait(false);
            __size += 1;

            var cooldowns = new List<PetSpellCooldown>();
            for (var i = 0; i < amountOfCooldowns; ++i) {
                cooldowns.Add(await Vanilla.PetSpellCooldown.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false));
                __size += 12;
            }

            optionalActionBars = new OptionalActionBars {
                Duration = duration,
                React = react,
                Command = command,
                Unknown = unknown,
                PetEnabled = petEnabled,
                ActionBars = actionBars,
                Spells = spells,
                Cooldowns = cooldowns,
            };
        }

        return new SMSG_PET_SPELLS {
            Pet = pet,
            ActionBars = optionalActionBars,
        };
    }

    internal int Size() {
        var size = 0;

        // pet: Generator.Generated.DataTypeGuid
        size += 8;

        if (ActionBars is { } actionBars) {
            // duration: Generator.Generated.DataTypeInteger
            size += 4;

            // react: Generator.Generated.DataTypeEnum
            size += 1;

            // command: Generator.Generated.DataTypeEnum
            size += 1;

            // unknown: Generator.Generated.DataTypeInteger
            size += 1;

            // pet_enabled: Generator.Generated.DataTypeEnum
            size += 1;

            // action_bars: Generator.Generated.DataTypeArray
            size += actionBars.ActionBars.Sum(e => 4);

            // amount_of_spells: Generator.Generated.DataTypeInteger
            size += 1;

            // spells: Generator.Generated.DataTypeArray
            size += actionBars.Spells.Sum(e => 4);

            // amount_of_cooldowns: Generator.Generated.DataTypeInteger
            size += 1;

            // cooldowns: Generator.Generated.DataTypeArray
            size += actionBars.Cooldowns.Sum(e => 12);

        }
        return size;
    }

}

