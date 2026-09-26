namespace Noof.Ledger.Application.Diagnostics;

// Read-only view of the file sink's static appsettings.json configuration, so the Log settings
// screen can show the operator where file retention lives without Web reading IConfiguration or
// naming Serilog itself.
public interface IFileLogSinkInfo
{
    string MinimumLevel { get; }
    int RetainedFileCountLimit { get; }
    long FileSizeLimitBytes { get; }
}
