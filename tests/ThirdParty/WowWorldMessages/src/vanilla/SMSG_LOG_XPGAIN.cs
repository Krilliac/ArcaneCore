using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

using ExperienceAwardTypeType = OneOf.OneOf<SMSG_LOG_XPGAIN.ExperienceAwardTypeNonKill, ExperienceAwardType>;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_LOG_XPGAIN: VanillaServerMessage, IWorldMessage {
    public class ExperienceAwardTypeNonKill {
        /// <summary>
        /// mangoszero sets to 1 and comments: 1 - none 0 - 100% group bonus output
        /// </summary>
        public required float ExpGroupBonus { get; set; }
        public required uint ExperienceWithoutRested { get; set; }
    }
    public required ulong Target { get; set; }
    public required uint TotalExp { get; set; }
    public required ExperienceAwardTypeType ExpType { get; set; }
    internal ExperienceAwardType ExpTypeValue => ExpType.Match(
        _ => Vanilla.ExperienceAwardType.NonKill,
        v => v
    );

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteULong(Target, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(TotalExp, cancellationToken).ConfigureAwait(false);

        await w.WriteByte((byte)ExpTypeValue, cancellationToken).ConfigureAwait(false);

        if (ExpType.Value is SMSG_LOG_XPGAIN.ExperienceAwardTypeNonKill experienceAwardTypeNonKill) {
            await w.WriteUInt(experienceAwardTypeNonKill.ExperienceWithoutRested, cancellationToken).ConfigureAwait(false);

            await w.WriteFloat(experienceAwardTypeNonKill.ExpGroupBonus, cancellationToken).ConfigureAwait(false);

        }

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 464, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 464, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_LOG_XPGAIN> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var target = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var totalExp = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        ExperienceAwardTypeType expType = (Vanilla.ExperienceAwardType)await r.ReadByte(cancellationToken).ConfigureAwait(false);

        if (expType.Value is Vanilla.ExperienceAwardType.NonKill) {
            var experienceWithoutRested = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

            var expGroupBonus = await r.ReadFloat(cancellationToken).ConfigureAwait(false);

            expType = new ExperienceAwardTypeNonKill {
                ExpGroupBonus = expGroupBonus,
                ExperienceWithoutRested = experienceWithoutRested,
            };
        }

        return new SMSG_LOG_XPGAIN {
            Target = target,
            TotalExp = totalExp,
            ExpType = expType,
        };
    }

    internal int Size() {
        var size = 0;

        // target: Generator.Generated.DataTypeGuid
        size += 8;

        // total_exp: Generator.Generated.DataTypeInteger
        size += 4;

        // exp_type: Generator.Generated.DataTypeEnum
        size += 1;

        if (ExpType.Value is SMSG_LOG_XPGAIN.ExperienceAwardTypeNonKill experienceAwardTypeNonKill) {
            // experience_without_rested: Generator.Generated.DataTypeInteger
            size += 4;

            // exp_group_bonus: Generator.Generated.DataTypeFloatingPoint
            size += 4;

        }

        return size;
    }

}

