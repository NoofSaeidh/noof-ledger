namespace Noof.Ledger.TestKit;

// A test that deletes its own temp directory right after disposing an in-process
// WebApplicationFactory<Program>, or an out-of-process host it spawned, races the OS: the file
// handle a rolling log sink held is not always released the moment the disposing call returns, so
// an immediate delete can throw IOException ("the process cannot access the file") or, on some
// Windows configurations, UnauthorizedAccessException. Every host-log cleanup in this solution goes
// through this one retrying, best-effort delete instead of its own copy of the same loop.
public static class BestEffortDelete
{
    public static async Task DirectoryAsync(string? path)
    {
        if (path is null || !Directory.Exists(path))
            return;

        const int maxAttempts = 5;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == maxAttempts)
                    return;

                await Task.Delay(TimeSpan.FromMilliseconds(200));
            }
        }
    }
}
