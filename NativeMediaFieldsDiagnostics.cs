using System.IO;
using System.Text.Json;

namespace YinggeVideoLibrary;

// Offline detail-page fixtures: no image or trailer is downloaded or played.
internal static class NativeMediaFieldsDiagnostics
{
    const string DetailUrl = "https://www.javdatabase.com/movies/abc-123/";
    const string FirstImage = "https://pics.dmm.co.jp/digital/video/abc00123/abc00123jp-1.jpg";
    const string SecondImage = "https://pics.dmm.co.jp/digital/video/abc00123/abc00123jp-2.jpg";
    const string Trailer = "https://cc3001.dmm.co.jp/litevideo/freepv/a/abc/abc00123/abc00123mhb.mp4";

    internal static IReadOnlyDictionary<string, object> Verify()
    {
        var findings = new Dictionary<string, object>(StringComparer.Ordinal);
        var candidate = new MovieMetadata
        {
            Code = "ABC-123", Title = "ABC-123", Source = "JAVDatabase", SourceUrl = DetailUrl
        };
        candidate.StampAvailableFields("搜索结果");
        var html = """
            <html><head><meta property='og:image' content='https://www.javdatabase.com/covers/full/ab/abc00123pl.webp'></head>
            <body><h1>ABC-123 Verified Example</h1>
            <p class='mb-1'><b>DVD ID:</b> ABC-123</p>
            <p class='mb-1'><b>Studio:</b> <a>Source Studio</a></p>
            <div class='image-gallery-section'>
              <a data-image-src='https://pics.dmm.co.jp/digital/video/abc00123/abc00123jp-1.jpg'
                 data-post-link='https://www.javdatabase.com/movies/abc-123/' data-post-title='ABC-123'></a>
              <a data-image-href='https://pics.dmm.co.jp/digital/video/abc00123/abc00123jp-2.jpg'
                 data-image-src='https://pics.dmm.co.jp/digital/video/abc00123/abc00123-2.jpg'
                 data-post-link='https://www.javdatabase.com/movies/abc-123/'></a>
              <a data-image-src='https://pics.dmm.co.jp/digital/video/abc00123/abc00123jp-1.jpg'></a>
              <a data-image-src='https://pics.dmm.co.jp/digital/video/abc00123/wrong.jpg'
                 data-post-link='https://www.javdatabase.com/movies/abc-999/'></a>
              <a data-image-src='https://pics.dmm.co.jp.evil.invalid/bad.jpg'></a>
              <a data-image-src='http://pics.dmm.co.jp/insecure.jpg'></a>
            </div>
            <video id='other'><source src='https://cc3001.dmm.co.jp/litevideo/freepv/a/abc/wrong.mp4'></video>
            <video id='jav-player'><source src='https://cc3001.dmm.co.jp/litevideo/freepv/a/abc/abc00123/abc00123mhb.mp4' type='video/mp4'></video>
            </body></html>
            """;
        var parsed = JavDatabaseScraper.ParseDetail(candidate, html);
        findings["nativePreviewAndTrailerExactDetail"] = parsed.DetailFetched && parsed.DetailIdentityVerified &&
            parsed.PreviewImageUrls.SequenceEqual([FirstImage, SecondImage]) && parsed.TrailerUrl == Trailer &&
            parsed.Studio == "Source Studio" &&
            JavDatabaseScraper.HasVerifiedMediaEvidence(parsed, nameof(MovieMetadata.PreviewImageUrls)) &&
            JavDatabaseScraper.HasVerifiedMediaEvidence(parsed, nameof(MovieMetadata.TrailerUrl)) &&
            parsed.FieldProvenance[nameof(MovieMetadata.PreviewImageUrls)].Source == "JAVDatabase" &&
            parsed.FieldProvenance[nameof(MovieMetadata.TrailerUrl)].SourceUrl == DetailUrl;

        var wrongHtml = html.Replace("<b>DVD ID:</b> ABC-123", "<b>DVD ID:</b> ABC-999", StringComparison.Ordinal);
        var rejectsWrongId = false;
        try { JavDatabaseScraper.ParseDetail(candidate, wrongHtml); }
        catch (ScrapeNotFoundException) { rejectsWrongId = true; }
        findings["nativeMediaRejectsWrongDetailIdentity"] = rejectsWrongId;

        findings["nativeMediaHttpsHostAllowlist"] =
            JavDatabaseScraper.IsTrustedPreviewUrl(FirstImage) &&
            JavDatabaseScraper.IsTrustedTrailerUrl(Trailer) &&
            !JavDatabaseScraper.IsTrustedPreviewUrl("https://pics.dmm.co.jp.evil.invalid/a.jpg") &&
            !JavDatabaseScraper.IsTrustedPreviewUrl("http://pics.dmm.co.jp/a.jpg") &&
            !JavDatabaseScraper.IsTrustedPreviewUrl("/relative.jpg") &&
            !JavDatabaseScraper.IsTrustedPreviewUrl("https://pics.dmm.co.jp:444/a.jpg") &&
            !JavDatabaseScraper.IsTrustedTrailerUrl("https://cc3001.dmm.co.jp/other/a.mp4") &&
            !JavDatabaseScraper.IsTrustedTrailerUrl("https://evil.invalid/a.mp4") &&
            !JavDatabaseScraper.IsVerifiedDetailSource("ABC-123", "https://www.javdatabase.com/movies/abc-999/");

        var copied = parsed.Clone();
        copied.PreviewImageUrls.RemoveAt(0);
        var restored = JsonSerializer.Deserialize<MovieMetadata>(JsonSerializer.Serialize(parsed));
        findings["nativeMediaCloneAndJsonRoundTrip"] = parsed.PreviewImageUrls.Count == 2 &&
            copied.PreviewImageUrls.Count == 1 && restored?.PreviewImageUrls.SequenceEqual(parsed.PreviewImageUrls) == true &&
            restored.TrailerUrl == Trailer &&
            JavDatabaseScraper.HasVerifiedMediaEvidence(restored, nameof(MovieMetadata.PreviewImageUrls));

        var primary = new MovieMetadata
        {
            Code = "ABC-123", Title = "Primary", Source = "JavDB", SourceUrl = "https://javdb.com/v/abc",
            DetailFetched = true, DetailIdentityVerified = true,
            PreviewImageUrls = ["https://evil.invalid/a.jpg"]
        };
        var combined = NativeMetadataAggregation.AggregateExactCode("ABC-123", [primary, parsed]);
        findings["nativeMediaAggregationPreservesSource"] = combined.PreviewImageUrls.SequenceEqual([FirstImage, SecondImage]) &&
            combined.TrailerUrl == Trailer && combined.FieldProvenance[nameof(MovieMetadata.TrailerUrl)].Source == "JAVDatabase" &&
            NativeMetadataAggregation.FieldObservations(combined, nameof(MovieMetadata.PreviewImageUrls)) is { Count: 1 } observations &&
            observations[0].Source == "JAVDatabase" && observations[0].SourceUrl == DetailUrl &&
            !combined.SourceConflictFields.Contains(nameof(MovieMetadata.PreviewImageUrls));

        var approved = ReviewFieldApproval.Build(null, combined, new Dictionary<string, ReviewFieldSelection>());
        var imageObservation = ReviewFieldApproval.Options(combined, nameof(MovieMetadata.PreviewImageUrls)).Single();
        var approvedFromSource = ReviewFieldApproval.Build(null, combined, new Dictionary<string, ReviewFieldSelection>
        {
            [nameof(MovieMetadata.PreviewImageUrls)] = new(ReviewFieldAction.Source, imageObservation)
        });
        findings["nativeMediaReviewAdoptsVerifiedOnly"] = approved.PreviewImageUrls.SequenceEqual([FirstImage, SecondImage]) &&
            approved.TrailerUrl == Trailer && JavDatabaseScraper.HasVerifiedMediaEvidence(approved, nameof(MovieMetadata.TrailerUrl)) &&
            approvedFromSource.PreviewImageUrls.SequenceEqual(parsed.PreviewImageUrls) &&
            approvedFromSource.FieldProvenance[nameof(MovieMetadata.PreviewImageUrls)].SourceUrl == DetailUrl;
        var unverified = parsed.Clone();
        unverified.DetailIdentityVerified = false;
        findings["nativeMediaUnverifiedHiddenFromReview"] =
            ReviewFieldApproval.Value(unverified, nameof(MovieMetadata.PreviewImageUrls)).Length == 0 &&
            ReviewFieldApproval.Options(unverified, nameof(MovieMetadata.PreviewImageUrls)).Count == 0 &&
            RejectsAdoption(unverified);
        var forged = combined.Clone();
        forged.SourceObservations.Add(new MetadataSourceObservation
        {
            Field = nameof(MovieMetadata.PreviewImageUrls), Value = "https://evil.invalid/a.jpg",
            Source = "JAVDatabase", SourceUrl = DetailUrl, Stage = "详情页"
        });
        findings["nativeMediaReviewFiltersForgedObservation"] =
            ReviewFieldApproval.Options(forged, nameof(MovieMetadata.PreviewImageUrls)).Count == 1;
        return findings;
    }

    static bool RejectsAdoption(MovieMetadata unverified)
    {
        try
        {
            ReviewFieldApproval.Build(null, unverified, new Dictionary<string, ReviewFieldSelection>
            {
                [nameof(MovieMetadata.PreviewImageUrls)] = new(ReviewFieldAction.Adopt)
            });
            return false;
        }
        catch (InvalidDataException) { return true; }
    }
}
