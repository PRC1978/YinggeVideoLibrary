namespace Yingge.Cloud.Accounts.Api;

// Resource polling must not recursively traverse an unbounded storage tree or
// fail when the retention worker removes a report during the measurement.
internal static class AdminStorageUsage
{
    internal static (long Bytes, bool Complete) Measure(string root, CancellationToken ct, int maxEntries = 10000)
    {
        long bytes = 0;
        var complete = true;
        if (!Directory.Exists(root)) return (0, true);
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
            var count = 0;
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            foreach (var path in Directory.EnumerateFiles(root, "*", options))
            {
                ct.ThrowIfCancellationRequested();
                if (++count > maxEntries || elapsed.Elapsed > TimeSpan.FromSeconds(1)) return (bytes, false);
                try { bytes += new FileInfo(path).Length; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { complete = false; }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { complete = false; }
        return (bytes, complete);
    }
}
