using System.IO;

namespace YinggeVideoLibrary;

internal static class MetadataRatingConcurrencyDiagnostics
{
    internal static IReadOnlyDictionary<string, bool> Run(string root)
    {
        Directory.CreateDirectory(root);
        const string id = "video-a";
        var first = new MovieMetadata
        {
            Title = "作品甲", Code = "ABC-123", Source = "TMDB 电影",
            SourceUrl = "https://www.themoviedb.org/movie/101", Summary = "旧简介"
        };
        MetadataStore.Save(root, new Dictionary<string, MovieMetadata> { [id] = first });
        var staleWindow = MetadataStore.Load(root)[id].Clone();
        var concurrent = staleWindow.Clone();
        concurrent.Summary = "另一窗口刚保存的新简介";
        MetadataStore.Save(root, new Dictionary<string, MovieMetadata> { [id] = concurrent });
        var score = new PlatformRating
        {
            Platform = "IMDb", SourceUrl = "https://www.imdb.com/title/tt0000101/",
            Value = 8.1, Maximum = 10, VoteCount = 123, MatchEvidence = "人工确认"
        };
        var updated = MetadataStore.AddRatingsForSameWork(root, id, staleWindow, [score]);
        var saved = MetadataStore.Load(root)[id];
        var preservesOtherEdit = updated.Summary == concurrent.Summary && saved.Summary == concurrent.Summary
            && saved.PlatformRatings.Count == 1 && saved.PlatformRatings[0].Platform == "IMDb";
        MetadataStore.AddRatingsForSameWork(root, id, staleWindow, [score]);
        var deduplicates = MetadataStore.Load(root)[id].PlatformRatings.Count == 1;
        var differentWork = saved.Clone();
        differentWork.SourceUrl = "https://www.themoviedb.org/movie/202";
        MetadataStore.Save(root, new Dictionary<string, MovieMetadata> { [id] = differentWork });
        var beforeRefusal = File.ReadAllBytes(Path.Combine(root, "metadata.json"));
        var refused = false;
        try { MetadataStore.AddRatingsForSameWork(root, id, staleWindow, [score]); }
        catch (InvalidOperationException) { refused = true; }
        return new Dictionary<string, bool>
        {
            ["ratingSavePreservesConcurrentMetadataEdit"] = preservesOtherEdit,
            ["ratingSaveDeduplicatesSamePlatform"] = deduplicates,
            ["ratingSaveRejectsChangedWorkIdentity"] = refused &&
                File.ReadAllBytes(Path.Combine(root, "metadata.json")).AsSpan().SequenceEqual(beforeRefusal)
        };
    }
}
