using System.IO;

namespace YinggeVideoLibrary;

/// <summary>
/// Best-effort removal of the per-run diagnostic directory.
///
/// A single <see cref="Directory.Delete(string, bool)"/> right after a run often
/// fails because antivirus, Explorer preview handlers or a just-killed child
/// still hold a handle for a few hundred milliseconds. Swallowing that failure
/// silently left <c>%TEMP%\YinggeFeatureTest-*</c> folders behind, so the caller
/// now gets a boolean and records the leftover path in its JSON report.
/// </summary>
internal static class TempDiagnosticDirectory
{
    const int Attempts = 3;
    const int RetryDelayMs = 200;

    internal static bool TryDelete(string path, int attempts = Attempts)
    {
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                if (!Directory.Exists(path)) return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == attempts) return false;
                Thread.Sleep(RetryDelayMs);
                continue;
            }
            if (attempt == attempts) return false;
            Thread.Sleep(RetryDelayMs);
        }
        return !Directory.Exists(path);
    }
}
