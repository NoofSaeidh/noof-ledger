namespace Noof.Ledger.Domain;

// Mirrors Noof.Ledger.Application.Receipts.ReceiptSource value for value: Domain has zero NuGet
// references and cannot depend on Application, so the two enums are kept in lockstep by
// ReceiptEnumTests rather than by a shared type.
public enum ReceiptSource
{
    FiscalQr = 0,
    Vision = 1,
}
