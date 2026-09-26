using Noof.Ledger.Application.Diagnostics;

namespace Noof.Ledger.Host.Logging;

internal sealed class FileLogSinkInfo(IConfiguration configuration) : IFileLogSinkInfo
{
    public string MinimumLevel => LoggingSetup.ResolveFileMinimumLevel(configuration).ToString();

    public int RetainedFileCountLimit => LoggingSetup.ResolveRetainedFileCountLimit(configuration);

    public long FileSizeLimitBytes => LoggingSetup.ResolveFileSizeLimitBytes(configuration);
}
