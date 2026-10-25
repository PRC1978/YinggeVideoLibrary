using System.IO;

namespace YinggeVideoLibrary;

internal static partial class AiJobStore
{
    // An AI prompt can mention a folder name inside free text. Rewriting only
    // JSON path values would leave an apparently valid but stale request that
    // a user could resend after relocation. Keep the result as evidence, and
    // make every unfinished request explicitly non-resumable instead.
    internal static void QuarantineRelocatedRequests(string backupPackage)
    {
        var directory = DirectoryPath(backupPackage);
        if (!Directory.Exists(directory)) return;
        foreach (var path in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            var id = Path.GetFileNameWithoutExtension(path);
            var job = Read(backupPackage, id);
            job.ProtectedRequest = null;
            if (job.Status != Completed)
            {
                job.Status = Canceled;
                job.LastErrorCode = "relocated-request";
                job.AttemptId = "";
                job.OwnerProcessId = 0;
                job.OwnerProcessStartedUtc = default;
                job.UpdatedUtc = DateTimeOffset.UtcNow;
            }
            Write(backupPackage, job, true);
        }
    }
}
