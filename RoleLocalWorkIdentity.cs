using System.IO;

namespace YinggeVideoLibrary;

// Hashing one explicitly selected file runs off the UI thread. A path-derived
// library ID, file name, timestamp or file size alone cannot prove the work
// has not been replaced at the same location.
internal static class RoleLocalWorkIdentity
{
    internal sealed record Capture(string Path, long Size, long ModifiedTicks, string ContentSha256);

    internal static async Task<Capture> CaptureAsync(VideoItem video,
        CancellationToken cancellationToken = default)
    {
        var path = System.IO.Path.GetFullPath(video.Path);
        var size = video.Size;
        var modifiedTicks = video.Modified.Ticks;
        var snapshot = new SiteListImportStore.LocalVideo(video.Id, video.Title, video.DisplayTitle,
            video.Code ?? "", "", "", "", path, size, modifiedTicks);
        var digest = await Task.Run(() => SiteListLocalActionStore.ComputeContentSha256(
            snapshot, cancellationToken), cancellationToken).ConfigureAwait(false);
        return new Capture(path, size, modifiedTicks, digest);
    }

    internal static void Apply(RoleProfileStore.WorkLink work, Capture capture)
    {
        work.LocalPath = capture.Path;
        work.LocalSize = capture.Size;
        work.LocalModifiedTicks = capture.ModifiedTicks;
        work.ContentSha256 = capture.ContentSha256;
    }

    internal static async Task<bool> MatchesAsync(VideoItem video, RoleProfileStore.WorkLink work,
        CancellationToken cancellationToken = default)
    {
        if (work.ContentSha256.Length != 64 || work.LocalPath.Length == 0 ||
            !video.Path.Equals(work.LocalPath, StringComparison.OrdinalIgnoreCase) ||
            !video.Id.Equals(work.LocalVideoId, StringComparison.OrdinalIgnoreCase)) return false;
        var snapshot = new FileInfo(video.Path);
        if (!snapshot.Exists || snapshot.Length != work.LocalSize) return false;
        // A changed timestamp is not automatically a different work, but a
        // full digest is always checked before following the relationship.
        var captured = new SiteListImportStore.LocalVideo(video.Id, video.Title,
            video.DisplayTitle, video.Code ?? "", "", "", "", video.Path,
            snapshot.Length, snapshot.LastWriteTime.Ticks);
        var digest = await Task.Run(() => SiteListLocalActionStore.ComputeContentSha256(
            captured, cancellationToken), cancellationToken).ConfigureAwait(false);
        return digest.Equals(work.ContentSha256, StringComparison.OrdinalIgnoreCase);
    }
}
