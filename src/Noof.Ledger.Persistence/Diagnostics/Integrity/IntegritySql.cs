namespace Noof.Ledger.Persistence.Diagnostics.Integrity;

// What the waiting checks share, each fragment written for a record aliased t.
internal static class IntegritySql
{
    // P-4: transactions has no updated_at, so a record's clock is its latest capture, job change or revision. A
    // Restore, a reply or a failed retry restarts the wait; a clock ahead of now never passes a threshold.
    public const string LastActivity = """
        GREATEST(
            t.created_at,
            (SELECT MAX(j.updated_at) FROM categorization_jobs j WHERE j.transaction_id = t.id),
            (SELECT MAX(r.created_at) FROM transaction_revisions r WHERE r.transaction_id = t.id))
        """;

    // Spec §1: a Vision receipt with no CategorizeReceipt job, or a Vision exchange slip with no RecordExchange job,
    // in any status. Only a candidate: IReceiptStore.IsAwaitingConfirmationAsync decides, never a SQL copy of it.
    public const string AwaitingCandidate = """
        EXISTS (
            SELECT 1 FROM receipts awaiting_receipt
            WHERE awaiting_receipt.transaction_id = t.id
              AND awaiting_receipt.source = 1
              AND NOT EXISTS (
                  SELECT 1 FROM categorization_jobs confirming_job
                  WHERE confirming_job.transaction_id = t.id
                    AND confirming_job.kind = CASE WHEN awaiting_receipt.receipt_kind = 6 THEN 6 ELSE 5 END))
        """;
}
