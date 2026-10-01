namespace Noof.Ledger.Domain;

// Why a first reading could not be recorded. Stored with Failed, so the echo - and a Cancel/Restore or a
// replayed update - asks for exactly what is missing instead of the generic failure.
public enum RecordFailureReason
{
    MissingReceivedAmount = 0,
    SameWallet = 1,
    LegCurrencyMismatch = 2,
    InvalidRate = 3,
    InvalidFee = 4,
    SlipIncomplete = 5,
    InvalidAmount = 6,
}
