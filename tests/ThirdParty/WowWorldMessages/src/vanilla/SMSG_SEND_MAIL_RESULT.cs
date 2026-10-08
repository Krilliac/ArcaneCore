using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

using MailActionType = OneOf.OneOf<SMSG_SEND_MAIL_RESULT.MailActionDeleted, SMSG_SEND_MAIL_RESULT.MailActionItemTaken, SMSG_SEND_MAIL_RESULT.MailActionMadePermanent, SMSG_SEND_MAIL_RESULT.MailActionMoneyTaken, SMSG_SEND_MAIL_RESULT.MailActionReturnedToSender, SMSG_SEND_MAIL_RESULT.MailActionSend, MailAction>;
using MailResultTwoType = OneOf.OneOf<SMSG_SEND_MAIL_RESULT.MailResultTwoErrEquipError, MailResultTwo>;
using MailResultType = OneOf.OneOf<SMSG_SEND_MAIL_RESULT.MailResultErrCannotSendToSelf, SMSG_SEND_MAIL_RESULT.MailResultErrCantSendWrappedCod, SMSG_SEND_MAIL_RESULT.MailResultErrDisabledForTrialAcc, SMSG_SEND_MAIL_RESULT.MailResultErrEquipError, SMSG_SEND_MAIL_RESULT.MailResultErrInternalError, SMSG_SEND_MAIL_RESULT.MailResultErrMailAndChatSuspended, SMSG_SEND_MAIL_RESULT.MailResultErrMailAttachmentInvalid, SMSG_SEND_MAIL_RESULT.MailResultErrNotEnoughMoney, SMSG_SEND_MAIL_RESULT.MailResultErrNotYourTeam, SMSG_SEND_MAIL_RESULT.MailResultErrRecipientCapReached, SMSG_SEND_MAIL_RESULT.MailResultErrRecipientNotFound, SMSG_SEND_MAIL_RESULT.MailResultErrTooManyAttachments, SMSG_SEND_MAIL_RESULT.MailResultOk, MailResult>;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_SEND_MAIL_RESULT: VanillaServerMessage, IWorldMessage {
    public class MailActionDeleted {
        public required MailResultTwoType Result2 { get; set; }
        internal MailResultTwo Result2Value => Result2.Match(
            _ => Vanilla.MailResultTwo.ErrEquipError,
            v => v
        );
    }
    public class MailActionItemTaken {
        public required MailResultType Result { get; set; }
        internal MailResult ResultValue => Result.Match(
            _ => Vanilla.MailResult.ErrCannotSendToSelf,
            _ => Vanilla.MailResult.ErrCantSendWrappedCod,
            _ => Vanilla.MailResult.ErrDisabledForTrialAcc,
            _ => Vanilla.MailResult.ErrEquipError,
            _ => Vanilla.MailResult.ErrInternalError,
            _ => Vanilla.MailResult.ErrMailAndChatSuspended,
            _ => Vanilla.MailResult.ErrMailAttachmentInvalid,
            _ => Vanilla.MailResult.ErrNotEnoughMoney,
            _ => Vanilla.MailResult.ErrNotYourTeam,
            _ => Vanilla.MailResult.ErrRecipientCapReached,
            _ => Vanilla.MailResult.ErrRecipientNotFound,
            _ => Vanilla.MailResult.ErrTooManyAttachments,
            _ => Vanilla.MailResult.Ok,
            v => v
        );
    }
    public class MailActionMadePermanent {
        public required MailResultTwoType Result2 { get; set; }
        internal MailResultTwo Result2Value => Result2.Match(
            _ => Vanilla.MailResultTwo.ErrEquipError,
            v => v
        );
    }
    public class MailActionMoneyTaken {
        public required MailResultTwoType Result2 { get; set; }
        internal MailResultTwo Result2Value => Result2.Match(
            _ => Vanilla.MailResultTwo.ErrEquipError,
            v => v
        );
    }
    public class MailActionReturnedToSender {
        public required MailResultTwoType Result2 { get; set; }
        internal MailResultTwo Result2Value => Result2.Match(
            _ => Vanilla.MailResultTwo.ErrEquipError,
            v => v
        );
    }
    public class MailActionSend {
        public required MailResultTwoType Result2 { get; set; }
        internal MailResultTwo Result2Value => Result2.Match(
            _ => Vanilla.MailResultTwo.ErrEquipError,
            v => v
        );
    }
    public class MailResultTwoErrEquipError {
        public required uint EquipError2 { get; set; }
    }
    public class MailResultErrCannotSendToSelf {
        /// <summary>
        /// cmangos/vmangos: item guid low?
        /// </summary>
        public required uint Item { get; set; }
        public required uint ItemCount { get; set; }
    }
    public class MailResultErrCantSendWrappedCod {
        /// <summary>
        /// cmangos/vmangos: item guid low?
        /// </summary>
        public required uint Item { get; set; }
        public required uint ItemCount { get; set; }
    }
    public class MailResultErrDisabledForTrialAcc {
        /// <summary>
        /// cmangos/vmangos: item guid low?
        /// </summary>
        public required uint Item { get; set; }
        public required uint ItemCount { get; set; }
    }
    public class MailResultErrEquipError {
        public required uint EquipError { get; set; }
    }
    public class MailResultErrInternalError {
        /// <summary>
        /// cmangos/vmangos: item guid low?
        /// </summary>
        public required uint Item { get; set; }
        public required uint ItemCount { get; set; }
    }
    public class MailResultErrMailAndChatSuspended {
        /// <summary>
        /// cmangos/vmangos: item guid low?
        /// </summary>
        public required uint Item { get; set; }
        public required uint ItemCount { get; set; }
    }
    public class MailResultErrMailAttachmentInvalid {
        /// <summary>
        /// cmangos/vmangos: item guid low?
        /// </summary>
        public required uint Item { get; set; }
        public required uint ItemCount { get; set; }
    }
    public class MailResultErrNotEnoughMoney {
        /// <summary>
        /// cmangos/vmangos: item guid low?
        /// </summary>
        public required uint Item { get; set; }
        public required uint ItemCount { get; set; }
    }
    public class MailResultErrNotYourTeam {
        /// <summary>
        /// cmangos/vmangos: item guid low?
        /// </summary>
        public required uint Item { get; set; }
        public required uint ItemCount { get; set; }
    }
    public class MailResultErrRecipientCapReached {
        /// <summary>
        /// cmangos/vmangos: item guid low?
        /// </summary>
        public required uint Item { get; set; }
        public required uint ItemCount { get; set; }
    }
    public class MailResultErrRecipientNotFound {
        /// <summary>
        /// cmangos/vmangos: item guid low?
        /// </summary>
        public required uint Item { get; set; }
        public required uint ItemCount { get; set; }
    }
    public class MailResultErrTooManyAttachments {
        /// <summary>
        /// cmangos/vmangos: item guid low?
        /// </summary>
        public required uint Item { get; set; }
        public required uint ItemCount { get; set; }
    }
    public class MailResultOk {
        /// <summary>
        /// cmangos/vmangos: item guid low?
        /// </summary>
        public required uint Item { get; set; }
        public required uint ItemCount { get; set; }
    }
    public required uint MailId { get; set; }
    public required MailActionType Action { get; set; }
    internal MailAction ActionValue => Action.Match(
        _ => Vanilla.MailAction.Deleted,
        _ => Vanilla.MailAction.ItemTaken,
        _ => Vanilla.MailAction.MadePermanent,
        _ => Vanilla.MailAction.MoneyTaken,
        _ => Vanilla.MailAction.ReturnedToSender,
        _ => Vanilla.MailAction.Send,
        v => v
    );

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt(MailId, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt((uint)ActionValue, cancellationToken).ConfigureAwait(false);

        if (Action.Value is SMSG_SEND_MAIL_RESULT.MailActionItemTaken mailActionItemTaken) {
            await w.WriteUInt((uint)mailActionItemTaken.ResultValue, cancellationToken).ConfigureAwait(false);

            if (mailActionItemTaken.Result.Value is SMSG_SEND_MAIL_RESULT.MailResultErrEquipError mailResultErrEquipError) {
                await w.WriteUInt(mailResultErrEquipError.EquipError, cancellationToken).ConfigureAwait(false);

            }
            else if (mailActionItemTaken.Result.Value is SMSG_SEND_MAIL_RESULT.MailResultOk mailResultOk) {
                await w.WriteUInt(mailResultOk.Item, cancellationToken).ConfigureAwait(false);

                await w.WriteUInt(mailResultOk.ItemCount, cancellationToken).ConfigureAwait(false);

            }
            else if (mailActionItemTaken.Result.Value is SMSG_SEND_MAIL_RESULT.MailResultErrCannotSendToSelf mailResultErrCannotSendToSelf) {
                await w.WriteUInt(mailResultErrCannotSendToSelf.Item, cancellationToken).ConfigureAwait(false);

                await w.WriteUInt(mailResultErrCannotSendToSelf.ItemCount, cancellationToken).ConfigureAwait(false);

            }
            else if (mailActionItemTaken.Result.Value is SMSG_SEND_MAIL_RESULT.MailResultErrNotEnoughMoney mailResultErrNotEnoughMoney) {
                await w.WriteUInt(mailResultErrNotEnoughMoney.Item, cancellationToken).ConfigureAwait(false);

                await w.WriteUInt(mailResultErrNotEnoughMoney.ItemCount, cancellationToken).ConfigureAwait(false);

            }
            else if (mailActionItemTaken.Result.Value is SMSG_SEND_MAIL_RESULT.MailResultErrRecipientNotFound mailResultErrRecipientNotFound) {
                await w.WriteUInt(mailResultErrRecipientNotFound.Item, cancellationToken).ConfigureAwait(false);

                await w.WriteUInt(mailResultErrRecipientNotFound.ItemCount, cancellationToken).ConfigureAwait(false);

            }
            else if (mailActionItemTaken.Result.Value is SMSG_SEND_MAIL_RESULT.MailResultErrNotYourTeam mailResultErrNotYourTeam) {
                await w.WriteUInt(mailResultErrNotYourTeam.Item, cancellationToken).ConfigureAwait(false);

                await w.WriteUInt(mailResultErrNotYourTeam.ItemCount, cancellationToken).ConfigureAwait(false);

            }
            else if (mailActionItemTaken.Result.Value is SMSG_SEND_MAIL_RESULT.MailResultErrInternalError mailResultErrInternalError) {
                await w.WriteUInt(mailResultErrInternalError.Item, cancellationToken).ConfigureAwait(false);

                await w.WriteUInt(mailResultErrInternalError.ItemCount, cancellationToken).ConfigureAwait(false);

            }
            else if (mailActionItemTaken.Result.Value is SMSG_SEND_MAIL_RESULT.MailResultErrDisabledForTrialAcc mailResultErrDisabledForTrialAcc) {
                await w.WriteUInt(mailResultErrDisabledForTrialAcc.Item, cancellationToken).ConfigureAwait(false);

                await w.WriteUInt(mailResultErrDisabledForTrialAcc.ItemCount, cancellationToken).ConfigureAwait(false);

            }
            else if (mailActionItemTaken.Result.Value is SMSG_SEND_MAIL_RESULT.MailResultErrRecipientCapReached mailResultErrRecipientCapReached) {
                await w.WriteUInt(mailResultErrRecipientCapReached.Item, cancellationToken).ConfigureAwait(false);

                await w.WriteUInt(mailResultErrRecipientCapReached.ItemCount, cancellationToken).ConfigureAwait(false);

            }
            else if (mailActionItemTaken.Result.Value is SMSG_SEND_MAIL_RESULT.MailResultErrCantSendWrappedCod mailResultErrCantSendWrappedCod) {
                await w.WriteUInt(mailResultErrCantSendWrappedCod.Item, cancellationToken).ConfigureAwait(false);

                await w.WriteUInt(mailResultErrCantSendWrappedCod.ItemCount, cancellationToken).ConfigureAwait(false);

            }
            else if (mailActionItemTaken.Result.Value is SMSG_SEND_MAIL_RESULT.MailResultErrMailAndChatSuspended mailResultErrMailAndChatSuspended) {
                await w.WriteUInt(mailResultErrMailAndChatSuspended.Item, cancellationToken).ConfigureAwait(false);

                await w.WriteUInt(mailResultErrMailAndChatSuspended.ItemCount, cancellationToken).ConfigureAwait(false);

            }
            else if (mailActionItemTaken.Result.Value is SMSG_SEND_MAIL_RESULT.MailResultErrTooManyAttachments mailResultErrTooManyAttachments) {
                await w.WriteUInt(mailResultErrTooManyAttachments.Item, cancellationToken).ConfigureAwait(false);

                await w.WriteUInt(mailResultErrTooManyAttachments.ItemCount, cancellationToken).ConfigureAwait(false);

            }
            else if (mailActionItemTaken.Result.Value is SMSG_SEND_MAIL_RESULT.MailResultErrMailAttachmentInvalid mailResultErrMailAttachmentInvalid) {
                await w.WriteUInt(mailResultErrMailAttachmentInvalid.Item, cancellationToken).ConfigureAwait(false);

                await w.WriteUInt(mailResultErrMailAttachmentInvalid.ItemCount, cancellationToken).ConfigureAwait(false);

            }

        }
        else if (Action.Value is SMSG_SEND_MAIL_RESULT.MailActionSend mailActionSend) {
            await w.WriteUInt((uint)mailActionSend.Result2Value, cancellationToken).ConfigureAwait(false);

            if (mailActionSend.Result2.Value is SMSG_SEND_MAIL_RESULT.MailResultTwoErrEquipError mailResultTwoErrEquipError) {
                await w.WriteUInt(mailResultTwoErrEquipError.EquipError2, cancellationToken).ConfigureAwait(false);

            }

        }
        else if (Action.Value is SMSG_SEND_MAIL_RESULT.MailActionMoneyTaken mailActionMoneyTaken) {
            await w.WriteUInt((uint)mailActionMoneyTaken.Result2Value, cancellationToken).ConfigureAwait(false);

            if (mailActionMoneyTaken.Result2.Value is SMSG_SEND_MAIL_RESULT.MailResultTwoErrEquipError mailResultTwoErrEquipError) {
                await w.WriteUInt(mailResultTwoErrEquipError.EquipError2, cancellationToken).ConfigureAwait(false);

            }

        }
        else if (Action.Value is SMSG_SEND_MAIL_RESULT.MailActionReturnedToSender mailActionReturnedToSender) {
            await w.WriteUInt((uint)mailActionReturnedToSender.Result2Value, cancellationToken).ConfigureAwait(false);

            if (mailActionReturnedToSender.Result2.Value is SMSG_SEND_MAIL_RESULT.MailResultTwoErrEquipError mailResultTwoErrEquipError) {
                await w.WriteUInt(mailResultTwoErrEquipError.EquipError2, cancellationToken).ConfigureAwait(false);

            }

        }
        else if (Action.Value is SMSG_SEND_MAIL_RESULT.MailActionDeleted mailActionDeleted) {
            await w.WriteUInt((uint)mailActionDeleted.Result2Value, cancellationToken).ConfigureAwait(false);

            if (mailActionDeleted.Result2.Value is SMSG_SEND_MAIL_RESULT.MailResultTwoErrEquipError mailResultTwoErrEquipError) {
                await w.WriteUInt(mailResultTwoErrEquipError.EquipError2, cancellationToken).ConfigureAwait(false);

            }

        }
        else if (Action.Value is SMSG_SEND_MAIL_RESULT.MailActionMadePermanent mailActionMadePermanent) {
            await w.WriteUInt((uint)mailActionMadePermanent.Result2Value, cancellationToken).ConfigureAwait(false);

            if (mailActionMadePermanent.Result2.Value is SMSG_SEND_MAIL_RESULT.MailResultTwoErrEquipError mailResultTwoErrEquipError) {
                await w.WriteUInt(mailResultTwoErrEquipError.EquipError2, cancellationToken).ConfigureAwait(false);

            }

        }

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 569, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 569, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_SEND_MAIL_RESULT> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var mailId = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        MailActionType action = (Vanilla.MailAction)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        if (action.Value is Vanilla.MailAction.ItemTaken) {
            MailResultType result = (Vanilla.MailResult)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

            if (result.Value is Vanilla.MailResult.ErrEquipError) {
                var equipError = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

                result = new MailResultErrEquipError {
                    EquipError = equipError,
                };
            }
            else if (result.Value is Vanilla.MailResult.Ok) {
                var item = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

                var itemCount = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

                result = new MailResultOk {
                    Item = item,
                    ItemCount = itemCount,
                };
            }
            else if (result.Value is Vanilla.MailResult.ErrCannotSendToSelf) {
                var item = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

                var itemCount = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

                result = new MailResultErrCannotSendToSelf {
                    Item = item,
                    ItemCount = itemCount,
                };
            }
            else if (result.Value is Vanilla.MailResult.ErrNotEnoughMoney) {
                var item = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

                var itemCount = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

                result = new MailResultErrNotEnoughMoney {
                    Item = item,
                    ItemCount = itemCount,
                };
            }
            else if (result.Value is Vanilla.MailResult.ErrRecipientNotFound) {
                var item = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

                var itemCount = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

                result = new MailResultErrRecipientNotFound {
                    Item = item,
                    ItemCount = itemCount,
                };
            }
            else if (result.Value is Vanilla.MailResult.ErrNotYourTeam) {
                var item = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

                var itemCount = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

                result = new MailResultErrNotYourTeam {
                    Item = item,
                    ItemCount = itemCount,
                };
            }
            else if (result.Value is Vanilla.MailResult.ErrInternalError) {
                var item = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

                var itemCount = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

                result = new MailResultErrInternalError {
                    Item = item,
                    ItemCount = itemCount,
                };
            }
            else if (result.Value is Vanilla.MailResult.ErrDisabledForTrialAcc) {
                var item = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

                var itemCount = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

                result = new MailResultErrDisabledForTrialAcc {
                    Item = item,
                    ItemCount = itemCount,
                };
            }
            else if (result.Value is Vanilla.MailResult.ErrRecipientCapReached) {
                var item = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

                var itemCount = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

                result = new MailResultErrRecipientCapReached {
                    Item = item,
                    ItemCount = itemCount,
                };
            }
            else if (result.Value is Vanilla.MailResult.ErrCantSendWrappedCod) {
                var item = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

                var itemCount = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

                result = new MailResultErrCantSendWrappedCod {
                    Item = item,
                    ItemCount = itemCount,
                };
            }
            else if (result.Value is Vanilla.MailResult.ErrMailAndChatSuspended) {
                var item = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

                var itemCount = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

                result = new MailResultErrMailAndChatSuspended {
                    Item = item,
                    ItemCount = itemCount,
                };
            }
            else if (result.Value is Vanilla.MailResult.ErrTooManyAttachments) {
                var item = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

                var itemCount = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

                result = new MailResultErrTooManyAttachments {
                    Item = item,
                    ItemCount = itemCount,
                };
            }
            else if (result.Value is Vanilla.MailResult.ErrMailAttachmentInvalid) {
                var item = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

                var itemCount = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

                result = new MailResultErrMailAttachmentInvalid {
                    Item = item,
                    ItemCount = itemCount,
                };
            }

            action = new MailActionItemTaken {
                Result = result,
            };
        }
        else if (action.Value is Vanilla.MailAction.Send) {
            MailResultTwoType result2 = (Vanilla.MailResultTwo)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

            if (result2.Value is Vanilla.MailResultTwo.ErrEquipError) {
                var equipError2 = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

                result2 = new MailResultTwoErrEquipError {
                    EquipError2 = equipError2,
                };
            }

            action = new MailActionSend {
                Result2 = result2,
            };
        }
        else if (action.Value is Vanilla.MailAction.MoneyTaken) {
            MailResultTwoType result2 = (Vanilla.MailResultTwo)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

            if (result2.Value is Vanilla.MailResultTwo.ErrEquipError) {
                var equipError2 = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

                result2 = new MailResultTwoErrEquipError {
                    EquipError2 = equipError2,
                };
            }

            action = new MailActionMoneyTaken {
                Result2 = result2,
            };
        }
        else if (action.Value is Vanilla.MailAction.ReturnedToSender) {
            MailResultTwoType result2 = (Vanilla.MailResultTwo)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

            if (result2.Value is Vanilla.MailResultTwo.ErrEquipError) {
                var equipError2 = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

                result2 = new MailResultTwoErrEquipError {
                    EquipError2 = equipError2,
                };
            }

            action = new MailActionReturnedToSender {
                Result2 = result2,
            };
        }
        else if (action.Value is Vanilla.MailAction.Deleted) {
            MailResultTwoType result2 = (Vanilla.MailResultTwo)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

            if (result2.Value is Vanilla.MailResultTwo.ErrEquipError) {
                var equipError2 = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

                result2 = new MailResultTwoErrEquipError {
                    EquipError2 = equipError2,
                };
            }

            action = new MailActionDeleted {
                Result2 = result2,
            };
        }
        else if (action.Value is Vanilla.MailAction.MadePermanent) {
            MailResultTwoType result2 = (Vanilla.MailResultTwo)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

            if (result2.Value is Vanilla.MailResultTwo.ErrEquipError) {
                var equipError2 = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

                result2 = new MailResultTwoErrEquipError {
                    EquipError2 = equipError2,
                };
            }

            action = new MailActionMadePermanent {
                Result2 = result2,
            };
        }

        return new SMSG_SEND_MAIL_RESULT {
            MailId = mailId,
            Action = action,
        };
    }

    internal int Size() {
        var size = 0;

        // mail_id: Generator.Generated.DataTypeInteger
        size += 4;

        // action: Generator.Generated.DataTypeEnum
        size += 4;

        if (Action.Value is SMSG_SEND_MAIL_RESULT.MailActionItemTaken mailActionItemTaken) {
            // result: Generator.Generated.DataTypeEnum
            size += 4;

            if (mailActionItemTaken.Result.Value is SMSG_SEND_MAIL_RESULT.MailResultErrEquipError mailResultErrEquipError) {
                // equip_error: Generator.Generated.DataTypeInteger
                size += 4;

            }
            else if (mailActionItemTaken.Result.Value is SMSG_SEND_MAIL_RESULT.MailResultOk mailResultOk) {
                // item: Generator.Generated.DataTypeItem
                size += 4;

                // item_count: Generator.Generated.DataTypeInteger
                size += 4;

            }
            else if (mailActionItemTaken.Result.Value is SMSG_SEND_MAIL_RESULT.MailResultErrCannotSendToSelf mailResultErrCannotSendToSelf) {
                // item: Generator.Generated.DataTypeItem
                size += 4;

                // item_count: Generator.Generated.DataTypeInteger
                size += 4;

            }
            else if (mailActionItemTaken.Result.Value is SMSG_SEND_MAIL_RESULT.MailResultErrNotEnoughMoney mailResultErrNotEnoughMoney) {
                // item: Generator.Generated.DataTypeItem
                size += 4;

                // item_count: Generator.Generated.DataTypeInteger
                size += 4;

            }
            else if (mailActionItemTaken.Result.Value is SMSG_SEND_MAIL_RESULT.MailResultErrRecipientNotFound mailResultErrRecipientNotFound) {
                // item: Generator.Generated.DataTypeItem
                size += 4;

                // item_count: Generator.Generated.DataTypeInteger
                size += 4;

            }
            else if (mailActionItemTaken.Result.Value is SMSG_SEND_MAIL_RESULT.MailResultErrNotYourTeam mailResultErrNotYourTeam) {
                // item: Generator.Generated.DataTypeItem
                size += 4;

                // item_count: Generator.Generated.DataTypeInteger
                size += 4;

            }
            else if (mailActionItemTaken.Result.Value is SMSG_SEND_MAIL_RESULT.MailResultErrInternalError mailResultErrInternalError) {
                // item: Generator.Generated.DataTypeItem
                size += 4;

                // item_count: Generator.Generated.DataTypeInteger
                size += 4;

            }
            else if (mailActionItemTaken.Result.Value is SMSG_SEND_MAIL_RESULT.MailResultErrDisabledForTrialAcc mailResultErrDisabledForTrialAcc) {
                // item: Generator.Generated.DataTypeItem
                size += 4;

                // item_count: Generator.Generated.DataTypeInteger
                size += 4;

            }
            else if (mailActionItemTaken.Result.Value is SMSG_SEND_MAIL_RESULT.MailResultErrRecipientCapReached mailResultErrRecipientCapReached) {
                // item: Generator.Generated.DataTypeItem
                size += 4;

                // item_count: Generator.Generated.DataTypeInteger
                size += 4;

            }
            else if (mailActionItemTaken.Result.Value is SMSG_SEND_MAIL_RESULT.MailResultErrCantSendWrappedCod mailResultErrCantSendWrappedCod) {
                // item: Generator.Generated.DataTypeItem
                size += 4;

                // item_count: Generator.Generated.DataTypeInteger
                size += 4;

            }
            else if (mailActionItemTaken.Result.Value is SMSG_SEND_MAIL_RESULT.MailResultErrMailAndChatSuspended mailResultErrMailAndChatSuspended) {
                // item: Generator.Generated.DataTypeItem
                size += 4;

                // item_count: Generator.Generated.DataTypeInteger
                size += 4;

            }
            else if (mailActionItemTaken.Result.Value is SMSG_SEND_MAIL_RESULT.MailResultErrTooManyAttachments mailResultErrTooManyAttachments) {
                // item: Generator.Generated.DataTypeItem
                size += 4;

                // item_count: Generator.Generated.DataTypeInteger
                size += 4;

            }
            else if (mailActionItemTaken.Result.Value is SMSG_SEND_MAIL_RESULT.MailResultErrMailAttachmentInvalid mailResultErrMailAttachmentInvalid) {
                // item: Generator.Generated.DataTypeItem
                size += 4;

                // item_count: Generator.Generated.DataTypeInteger
                size += 4;

            }

        }
        else if (Action.Value is SMSG_SEND_MAIL_RESULT.MailActionSend mailActionSend) {
            // result2: Generator.Generated.DataTypeEnum
            size += 4;

            if (mailActionSend.Result2.Value is SMSG_SEND_MAIL_RESULT.MailResultTwoErrEquipError mailResultTwoErrEquipError) {
                // equip_error2: Generator.Generated.DataTypeInteger
                size += 4;

            }

        }
        else if (Action.Value is SMSG_SEND_MAIL_RESULT.MailActionMoneyTaken mailActionMoneyTaken) {
            // result2: Generator.Generated.DataTypeEnum
            size += 4;

            if (mailActionMoneyTaken.Result2.Value is SMSG_SEND_MAIL_RESULT.MailResultTwoErrEquipError mailResultTwoErrEquipError) {
                // equip_error2: Generator.Generated.DataTypeInteger
                size += 4;

            }

        }
        else if (Action.Value is SMSG_SEND_MAIL_RESULT.MailActionReturnedToSender mailActionReturnedToSender) {
            // result2: Generator.Generated.DataTypeEnum
            size += 4;

            if (mailActionReturnedToSender.Result2.Value is SMSG_SEND_MAIL_RESULT.MailResultTwoErrEquipError mailResultTwoErrEquipError) {
                // equip_error2: Generator.Generated.DataTypeInteger
                size += 4;

            }

        }
        else if (Action.Value is SMSG_SEND_MAIL_RESULT.MailActionDeleted mailActionDeleted) {
            // result2: Generator.Generated.DataTypeEnum
            size += 4;

            if (mailActionDeleted.Result2.Value is SMSG_SEND_MAIL_RESULT.MailResultTwoErrEquipError mailResultTwoErrEquipError) {
                // equip_error2: Generator.Generated.DataTypeInteger
                size += 4;

            }

        }
        else if (Action.Value is SMSG_SEND_MAIL_RESULT.MailActionMadePermanent mailActionMadePermanent) {
            // result2: Generator.Generated.DataTypeEnum
            size += 4;

            if (mailActionMadePermanent.Result2.Value is SMSG_SEND_MAIL_RESULT.MailResultTwoErrEquipError mailResultTwoErrEquipError) {
                // equip_error2: Generator.Generated.DataTypeInteger
                size += 4;

            }

        }

        return size;
    }

}

