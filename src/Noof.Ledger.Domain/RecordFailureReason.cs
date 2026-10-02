namespace Noof.Ledger.Domain;

// Why a first reading could not be recorded. Stored with Failed, so the echo - and a Cancel/Restore or a
// replayed update - asks for exactly what is missing instead of the generic failure. None for every record
// that is not Failed, and for a failure that names no reason.
public enum RecordFailureReason
{
    None = 0,
    MissingReceivedAmount = 1,
    SameWallet = 2,
    LegCurrencyMismatch = 3,
    InvalidRate = 4,
    InvalidFee = 5,
    SlipIncomplete = 6,
    InvalidAmount = 7,
}
