namespace WowWorldMessages.Vanilla;

public enum MailResult : uint {
    Ok = 0,
    ErrEquipError = 1,
    ErrCannotSendToSelf = 2,
    ErrNotEnoughMoney = 3,
    ErrRecipientNotFound = 4,
    ErrNotYourTeam = 5,
    ErrInternalError = 6,
    ErrDisabledForTrialAcc = 14,
    ErrRecipientCapReached = 15,
    ErrCantSendWrappedCod = 16,
    ErrMailAndChatSuspended = 17,
    ErrTooManyAttachments = 18,
    ErrMailAttachmentInvalid = 19,
}

