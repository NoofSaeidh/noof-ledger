using Microsoft.Extensions.AI;

namespace Noof.Ledger.Ai;

// The one seam between categorisation and whichever provider answers it. An implementation owns
// everything provider-specific - the SDK, the stored secret, the model id, how a strict tool is
// said on the wire, what the provider's exceptions mean - and lives in that provider's own folder.
internal interface IChatClientFactory
{
    // A new client per call, never cached: the stored secret can change between two jobs.
    Task<IChatClient> CreateAsync(CancellationToken cancellationToken);
}
