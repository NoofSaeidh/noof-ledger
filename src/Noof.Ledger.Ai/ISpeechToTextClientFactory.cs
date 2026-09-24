using Microsoft.Extensions.AI;

namespace Noof.Ledger.Ai;

// The one seam between transcription and whichever provider answers it, as IChatClientFactory is for the chat side.
// An implementation owns everything provider-specific and lives in that provider's own folder.
internal interface ISpeechToTextClientFactory
{
    // A new client per call, never cached: the stored secret can change between two jobs.
    Task<ISpeechToTextClient> CreateAsync(CancellationToken cancellationToken);
}
