using System.Text.Json;

namespace YinggeVideoLibrary;

internal static class NativeMetadataAggregationDiagnostics
{
    internal static Dictionary<string, bool> Verify()
    {
        var first = new MovieMetadata
        {
            Code = "ABP-123", Title = "同じ作品", Source = "JavDB",
            SourceUrl = "https://javdb.com/v/example", ReleaseDate = "2023-05-01",
            Actors = ["演员甲"], Rating = "4.2/5",
            DetailFetched = true, DetailIdentityVerified = true
        };
        first.StampAvailableFields("详情页");
        var second = new MovieMetadata
        {
            Code = "ABP-123", Title = "同じ作品", Source = "JAVDatabase",
            SourceUrl = "https://www.javdatabase.com/movies/ABP-123/", ReleaseDate = "2023/05/01",
            Studio = "制作方乙", Actors = ["演员甲"], Rating = "8.0/10",
            DetailFetched = true, DetailIdentityVerified = true
        };
        second.StampAvailableFields("详情页");
        var merged = NativeMetadataAggregation.AggregateExactCode("ABP-123", [first, second]);
        var weighted = NativeMetadataAggregation.AggregateExactCode("ABP-123",
            NativeMetadataAggregation.OrderByTrust([first, second], source => source == "JAVDatabase" ? 20 : 10));
        var tieA = NativeMetadataAggregation.OrderByTrust([first, second], _ => 10);
        var tieB = NativeMetadataAggregation.OrderByTrust([second, first], _ => 10);
        var translated = second.Clone();
        translated.Title = "Same Work";
        translated.OriginalTitle = "同じ作品";
        translated.StampAvailableFields("详情页");
        var withTranslation = NativeMetadataAggregation.AggregateExactCode("ABP-123", [first, translated]);
        var withConflict = second.Clone();
        withConflict.Title = "別の作品";
        withConflict.ReleaseDate = "2024-06-01";
        withConflict.StampAvailableFields("详情页");
        var disputed = NativeMetadataAggregation.AggregateExactCode("ABP-123", [first, withConflict]);
        var handApproved = new AiReviewRecord { Status = "approved", DecisionSource = "manual" };
        var serialized = JsonSerializer.Serialize(disputed);
        var restored = JsonSerializer.Deserialize<MovieMetadata>(serialized);
        var wrongCode = second.Clone(); wrongCode.Code = "ABP-124";
        var unverified = second.Clone(); unverified.DetailIdentityVerified = false;
        var paid = second.Clone(); paid.Source = "Javinfo · JAVDatabase";
        return new Dictionary<string, bool>
        {
            ["nativeAggregationFillsBlankWithProvenance"] = merged.Studio == "制作方乙"
                && merged.FieldProvenance[nameof(MovieMetadata.Studio)].Source == "JAVDatabase"
                && merged.Title == first.Title,
            ["nativeAggregationKeepsSourceValues"] = NativeMetadataAggregation.FieldObservations(merged,
                    nameof(MovieMetadata.Rating)).Count == 2
                && merged.SourceObservations.Any(item => item.Source == "JavDB" && item.SourceUrl == first.SourceUrl)
                && merged.SourceObservations.Any(item => item.Source == "JAVDatabase" && item.SourceUrl == second.SourceUrl),
            ["nativeAggregationMultipleRatings"] = RatingAggregation.Summarize(merged.PlatformRatings) is
                { SourceCount: 2 } && merged.SourceConflictFields.Count == 0,
            ["nativeAggregationTrustOnlyChoosesPrimary"] = weighted.Source == "JAVDatabase"
                && weighted.FieldProvenance[nameof(MovieMetadata.Title)].Source == "JAVDatabase"
                && weighted.SourceObservations.Count == merged.SourceObservations.Count
                && tieA.Select(item => item.Source).SequenceEqual(tieB.Select(item => item.Source)),
            ["nativeAggregationConflictsRemainPending"] = disputed.Title == first.Title
                && disputed.SourceConflictFields.Contains(nameof(MovieMetadata.Title))
                && disputed.SourceConflictFields.Contains(nameof(MovieMetadata.ReleaseDate))
                && NativeMetadataAggregation.RequiresManualReview(disputed, null),
            ["nativeAggregationTranslatedTitleNotConflict"] =
                withTranslation.SourceConflictFields.Count == 0
                && NativeMetadataAggregation.FieldObservations(withTranslation, nameof(MovieMetadata.Title)).Count == 2,
            ["nativeAggregationManualDecisionKeepsOriginal"] =
                NativeMetadataAggregation.RequiresManualReview(merged, handApproved)
                && !NativeMetadataAggregation.RequiresManualReview(merged, null),
            ["nativeAggregationRejectsWrongIdentity"] = ThrowsWithoutVerified(wrongCode)
                && ThrowsWithoutVerified(unverified) && ThrowsWithoutVerified(paid),
            ["nativeAggregationPersistsOfflineEvidence"] = restored is not null
                && restored.SourceConflictFields.Contains(nameof(MovieMetadata.Title))
                && restored.SourceObservations.Count == disputed.SourceObservations.Count
                && restored.Clone().SourceObservations[0].SourceUrl == first.SourceUrl
        };
    }

    static bool ThrowsWithoutVerified(MovieMetadata record)
    {
        try { NativeMetadataAggregation.AggregateExactCode("ABP-123", [record]); return false; }
        catch (InvalidOperationException) { return true; }
    }
}
