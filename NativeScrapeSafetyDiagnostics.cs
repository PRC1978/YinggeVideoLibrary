using System.IO;
using System.Net;

namespace YinggeVideoLibrary;

internal static class NativeScrapeSafetyDiagnostics
{
    internal static async Task<IReadOnlyDictionary<string, bool>> VerifyAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        const string videoId = "video-A";
        var approved = new byte[] { 1, 2, 3, 4 };
        var firstBytes = new byte[] { 10, 11, 12, 13 };
        var secondBytes = new byte[] { 20, 21, 22, 23 };
        var legacyPath = Path.Combine(directory, "posters", videoId + ".jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
        await File.WriteAllBytesAsync(legacyPath, approved);
        var first = await CandidatePosterStore.StoreAsync(directory, videoId, firstBytes);
        var second = await CandidatePosterStore.StoreAsync(directory, videoId, secondBytes);
        var repeated = await CandidatePosterStore.StoreAsync(directory, videoId, firstBytes);
        var separateCandidates = first != second && repeated == first
            && string.Equals(Path.GetDirectoryName(first), Path.Combine(directory, "posters"), StringComparison.OrdinalIgnoreCase)
            && (await File.ReadAllBytesAsync(first)).SequenceEqual(firstBytes)
            && (await File.ReadAllBytesAsync(second)).SequenceEqual(secondBytes)
            && (await File.ReadAllBytesAsync(legacyPath)).SequenceEqual(approved);

        // Even a damaged content-addressed path can already be referenced by
        // an approved record. A new candidate must leave that path untouched.
        var altered = new byte[] { 90, 91, 92, 93 };
        await File.WriteAllBytesAsync(first, altered);
        var replacement = await CandidatePosterStore.StoreAsync(directory, videoId, firstBytes);
        var existingCandidateUntouched = replacement != first
            && (await File.ReadAllBytesAsync(first)).SequenceEqual(altered)
            && (await File.ReadAllBytesAsync(replacement)).SequenceEqual(firstBytes);

        var candidate = new MovieMetadata { Code = "ABC-123", Source = "JavDB" };
        var verified = candidate.Clone();
        verified.DetailFetched = true;
        verified.DetailIdentityVerified = true;
        var unverified = verified.Clone();
        unverified.DetailIdentityVerified = false;
        var notFetched = verified.Clone();
        notFetched.DetailFetched = false;
        var verifiedAccepted = ReferenceEquals(verified,
            await JavDbScraper.RequireVerifiedDetailsAsync(candidate, (_, _) => Task.FromResult(verified)));
        var unverifiedBlocked = await RejectsUnverifiedAsync(candidate, unverified)
            && await RejectsUnverifiedAsync(candidate, notFetched);

        var limited = new ScrapeHttpException("rate limited", HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(30));
        var timeout = new TaskCanceledException("detail request timed out");
        var rateLimitPropagated = await PropagatesAsync(candidate, limited);
        var timeoutPropagated = await PropagatesAsync(candidate, timeout);
        return new Dictionary<string, bool>
        {
            ["candidatePostersDoNotOverwriteApproved"] = separateCandidates,
            ["candidatePosterNeverOverwritesExistingPath"] = existingCandidateUntouched,
            ["javDbNumberRequiresVerifiedDetail"] = verifiedAccepted && unverifiedBlocked,
            ["javDbDetailRateLimitPropagates"] = rateLimitPropagated,
            ["javDbDetailTimeoutPropagates"] = timeoutPropagated
        };
    }

    static async Task<bool> RejectsUnverifiedAsync(MovieMetadata candidate, MovieMetadata result)
    {
        try
        {
            _ = await JavDbScraper.RequireVerifiedDetailsAsync(candidate, (_, _) => Task.FromResult(result));
            return false;
        }
        catch (InvalidDataException) { return true; }
    }

    static async Task<bool> PropagatesAsync(MovieMetadata candidate, Exception error)
    {
        try
        {
            _ = await JavDbScraper.RequireVerifiedDetailsAsync(candidate,
                (_, _) => Task.FromException<MovieMetadata>(error));
            return false;
        }
        catch (Exception caught) { return ReferenceEquals(caught, error); }
    }
}
