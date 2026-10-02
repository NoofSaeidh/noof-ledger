namespace Noof.Ledger.Application.Receipts;

public enum SlipDisposition { Record = 0, Hold = 1, Incomplete = 2 }

public enum SlipProblem { AmountsDisagree = 0, TaxIdUnreadable = 1, SlipNumberUnreadable = 2 }

public enum SlipMissing { GivenAmount = 0, GivenCurrency = 1, ReceivedAmount = 2, ReceivedCurrency = 3 }

public sealed record SlipAssessment(SlipDisposition Disposition, IReadOnlyList<SlipProblem> Problems, IReadOnlyList<SlipMissing> Missing);
