using Noof.Ledger.Application.Categorization;

namespace Noof.Ledger.Application.Chat;

public interface IRecordEcho
{
    string Acknowledgement { get; }
    string Correcting { get; }
    string EditPrompt { get; }
    EchoMessage Failure { get; }

    EchoMessage Compose(CategorizationSubject record);
    EchoMessage ComposeCorrectionFailure(CategorizationSubject record);
}
