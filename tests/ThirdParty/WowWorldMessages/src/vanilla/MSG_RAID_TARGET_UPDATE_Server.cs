using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

using RaidTargetUpdateTypeType = OneOf.OneOf<MSG_RAID_TARGET_UPDATE_Server.RaidTargetUpdateTypeFull, MSG_RAID_TARGET_UPDATE_Server.RaidTargetUpdateTypePartial, RaidTargetUpdateType>;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class MSG_RAID_TARGET_UPDATE_Server: VanillaServerMessage, IWorldMessage {
    public class RaidTargetUpdateTypeFull {
        public const int RaidTargetsLength = 8;
        public required RaidTargetUpdate[] RaidTargets { get; set; }
    }
    public class RaidTargetUpdateTypePartial {
        public required RaidTargetUpdate RaidTarget { get; set; }
    }
    public required RaidTargetUpdateTypeType UpdateType { get; set; }
    internal RaidTargetUpdateType UpdateTypeValue => UpdateType.Match(
        _ => Vanilla.RaidTargetUpdateType.Full,
        _ => Vanilla.RaidTargetUpdateType.Partial,
        v => v
    );

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteByte((byte)UpdateTypeValue, cancellationToken).ConfigureAwait(false);

        if (UpdateType.Value is MSG_RAID_TARGET_UPDATE_Server.RaidTargetUpdateTypeFull raidTargetUpdateTypeFull) {
            foreach (var v in raidTargetUpdateTypeFull.RaidTargets) {
                await v.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
            }

        }
        else if (UpdateType.Value is MSG_RAID_TARGET_UPDATE_Server.RaidTargetUpdateTypePartial raidTargetUpdateTypePartial) {
            await raidTargetUpdateTypePartial.RaidTarget.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);

        }

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 801, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 801, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<MSG_RAID_TARGET_UPDATE_Server> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        RaidTargetUpdateTypeType updateType = (Vanilla.RaidTargetUpdateType)await r.ReadByte(cancellationToken).ConfigureAwait(false);

        if (updateType.Value is Vanilla.RaidTargetUpdateType.Full) {
            var raidTargets = new RaidTargetUpdate[RaidTargetUpdateTypeFull.RaidTargetsLength];
            for (var i = 0; i < RaidTargetUpdateTypeFull.RaidTargetsLength; ++i) {
                raidTargets[i] = await Vanilla.RaidTargetUpdate.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false);
            }

            updateType = new RaidTargetUpdateTypeFull {
                RaidTargets = raidTargets,
            };
        }
        else if (updateType.Value is Vanilla.RaidTargetUpdateType.Partial) {
            var raidTarget = await RaidTargetUpdate.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false);

            updateType = new RaidTargetUpdateTypePartial {
                RaidTarget = raidTarget,
            };
        }

        return new MSG_RAID_TARGET_UPDATE_Server {
            UpdateType = updateType,
        };
    }

    internal int Size() {
        var size = 0;

        // update_type: Generator.Generated.DataTypeEnum
        size += 1;

        if (UpdateType.Value is MSG_RAID_TARGET_UPDATE_Server.RaidTargetUpdateTypeFull raidTargetUpdateTypeFull) {
            // raid_targets: Generator.Generated.DataTypeArray
            size += raidTargetUpdateTypeFull.RaidTargets.Sum(e => 9);

        }
        else if (UpdateType.Value is MSG_RAID_TARGET_UPDATE_Server.RaidTargetUpdateTypePartial raidTargetUpdateTypePartial) {
            // raid_target: Generator.Generated.DataTypeStruct
            size += 9;

        }

        return size;
    }

}

