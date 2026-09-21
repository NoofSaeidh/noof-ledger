using Noof.Ledger.Domain;

namespace Noof.Ledger.Application.Jobs;

public interface IJobQueue
{
    // Claims one pending, due job for workerId and marks it Claimed. lease is how long the
    // claim is held before ReleaseExpiredLeasesAsync may release it back to Pending - there is
    // no separate lease-expiry column, so claiming sets RunAfter to now + lease. Returns null
    // when nothing is pending and due.
    Task<CategorizationJob?> ClaimAsync(string workerId, TimeSpan lease, CancellationToken cancellationToken);

    Task SucceedAsync(Guid jobId, CancellationToken cancellationToken);

    // Returns the job to Pending at runAfter, unless the job is already at the attempt cap,
    // in which case it goes straight to Failed instead and runAfter is ignored.
    Task RetryAsync(Guid jobId, DateTimeOffset runAfter, string error, CancellationToken cancellationToken);

    Task FailAsync(Guid jobId, string error, CancellationToken cancellationToken);

    // Returns claimed jobs whose lease (RunAfter) has passed to Pending. Intended to run on
    // every poll tick, never as a startup sweep - a startup sweep would steal live work from
    // a second Host process.
    Task<int> ReleaseExpiredLeasesAsync(DateTimeOffset now, CancellationToken cancellationToken);
}
