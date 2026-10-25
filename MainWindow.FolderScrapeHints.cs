using System.IO;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    // This hint is never enough to approve a weak title on its own. The caller
    // should keep the review below ReviewManualThreshold when only this bonus
    // would cross that threshold. Actor evidence is stronger than title text.
    int GetFolderPersonEvidence(VideoItem video, MovieMetadata candidate, out string reason)
    {
        reason = "";
        if (!config.UseFolderPersonHint) return 0;
        var person = ScrapeSearchHints.FindPersonHint(video.Path, config.Folders);
        if (person is null) return 0;
        if (candidate.Actors?.Any(actor => ScrapeSearchHints.MatchesPerson(person, actor)) == true)
        {
            reason = $"目录人物线索「{person}」与候选演员一致";
            return 8;
        }
        if (ScrapeSearchHints.MatchesPerson(person, candidate.Title))
        {
            reason = $"目录人物线索「{person}」出现在候选标题中，仍需核对作品";
            return 2;
        }
        return 0;
    }

    // Retry once only when the first title search found nothing or its best
    // title evidence is weak. The outgoing query contains the original title
    // and one vetted directory leaf, never a drive, full path, or other folder.
    async Task<List<MovieMetadata>> SearchWithFolderPersonHintAsync(
        VideoItem video, string title, int selectedSource,
        IReadOnlyList<MovieMetadata> firstCandidates,
        CancellationToken cancellationToken = default, bool bulk = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var originals = firstCandidates.ToList();
        if (selectedSource is 8 or 10 or 13) return originals; // Manual key-backed sources get no implicit second request.
        if (!config.UseFolderPersonHint) return originals;
        if (originals.Count > 0 && originals.Max(candidate => LocalScrapeConfidence(video, candidate, out _)) >= 65)
            return originals;

        var person = ScrapeSearchHints.FindPersonHint(video.Path, config.Folders);
        if (person is null || string.IsNullOrWhiteSpace(title) || ScrapeSearchHints.MatchesPerson(person, title))
            return originals;
        if (title.Contains('\\') || title.Contains('/') ||
            (title.Length >= 2 && char.IsLetter(title[0]) && title[1] == ':'))
            return originals;
        var cleanedTitle = AdditionalScrapers.CleanTitle(title).Trim();
        if (cleanedTitle.Length == 0) return originals;
        if (cleanedTitle.Length > 160) cleanedTitle = cleanedTitle[..160];
        var query = cleanedTitle + " " + person;

        try
        {
            var extra = await SearchCandidatesAsync(query, selectedSource, bulk, cancellationToken,
                video: video);
            cancellationToken.ThrowIfCancellationRequested();
            return originals.Concat(extra)
                .GroupBy(item => item.Source + "|" + item.SourceUrl + "|" + item.Title,
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First()).ToList();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (originals.Count > 0)
        {
            // A failed optional query must not discard viable first-pass
            // candidates. When there were none, propagate the uncertainty.
            Log("人物线索辅助查询失败，继续使用标题候选：" + ex.Message);
            return originals;
        }
    }
}
