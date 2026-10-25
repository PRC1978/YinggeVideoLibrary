using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace YinggeVideoLibrary;

// Search candidates must not share the mutable legacy posters/<videoId>.jpg
// path with an already approved record. The file name includes the content
// hash, and an existing file is only read, never replaced.
internal static class CandidatePosterStore
{
    internal static async Task<string> StoreAsync(string dataDir, string videoId, byte[] bytes,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(dataDir) || string.IsNullOrWhiteSpace(videoId) || bytes.Length == 0)
            throw new ArgumentException("候选封面缺少保存位置、视频标识或图片内容。");
        var contentHash = SHA256.HashData(bytes);
        var videoHash = SHA256.HashData(Encoding.UTF8.GetBytes(videoId.Trim().ToUpperInvariant()));
        // Keep files at the managed posters root: existing backup/restore
        // includes safe top-level image names and can preserve these paths.
        var folder = Path.Combine(dataDir, "posters");
        var stem = Convert.ToHexString(videoHash) + "-" + Convert.ToHexString(contentHash);
        var destination = Path.Combine(folder, stem + ".jpg");
        Directory.CreateDirectory(folder);
        if (await ExistingMatchesAsync(destination, contentHash, bytes.Length, cancellationToken)) return destination;

        var temporary = Path.Combine(folder, "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            // A corrupt/tampered hash path might be referenced by an approved
            // record. Never overwrite it; use a fresh filename instead.
            if (File.Exists(destination)) destination = UniquePath(folder, stem);
            try { File.Move(temporary, destination); }
            catch (IOException) when (File.Exists(destination))
            {
                if (await ExistingMatchesAsync(destination, contentHash, bytes.Length, cancellationToken))
                    return destination;
                destination = UniquePath(folder, stem);
                File.Move(temporary, destination);
            }
            return destination;
        }
        finally { try { File.Delete(temporary); } catch { } }
    }

    static string UniquePath(string folder, string stem) =>
        Path.Combine(folder, stem + "-" + Guid.NewGuid().ToString("N") + ".jpg");

    static async Task<bool> ExistingMatchesAsync(string path, byte[] expectedHash, int expectedLength,
        CancellationToken cancellationToken)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length != expectedLength) return false;
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var actualHash = await SHA256.HashDataAsync(input, cancellationToken);
            return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
