namespace YinggeVideoLibrary;

public partial class MainWindow
{
    // Manual title search uses the same verified, per-field native aggregation
    // as the number queue. A failed second source leaves a review candidate,
    // never an apparently complete automatic approval.
    async Task<(MovieMetadata Record, bool ForcePending)> AggregateNativeTitleCandidateAsync(
        MovieMetadata chosen, string videoId, CancellationToken cancellationToken = default)
    {
        if (!NativeMetadataAggregation.IsNativeSource(chosen.Source) ||
            !chosen.DetailFetched || !chosen.DetailIdentityVerified ||
            !NativeMetadataAggregation.HasExactCode(chosen, chosen.Code))
            return (chosen, false);

        var otherSource = chosen.Source == "JavDB" ? "JAVDatabase" : "JavDB";
        var otherEnabled = otherSource == "JavDB"
            ? JavDbEnabledBox.IsChecked == true || javDbTitleEnabledBox?.IsChecked == true
            : javDatabaseEnabledBox?.IsChecked == true;
        if (!otherEnabled) return (chosen, false);

        try
        {
            var other = otherSource == "JavDB"
                ? await JavDbScraper.ScrapeAsync(chosen.Code, videoId, DataDir, cancellationToken)
                : await JavDatabaseScraper.ScrapeByCodeAsync(chosen.Code, cancellationToken);
            if (!other.DetailFetched || !other.DetailIdentityVerified ||
                !NativeMetadataAggregation.HasExactCode(other, chosen.Code))
                throw new InvalidOperationException(otherSource + " 的详情没有独立核对同一番号。");
            var ordered = NativeMetadataAggregation.OrderByTrust([chosen, other], SourceTrust);
            return (NativeMetadataAggregation.AggregateExactCode(chosen.Code, ordered), false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (ScrapeNotFoundException)
        {
            var limited = chosen.Clone();
            limited.DetailNote = JoinNativeDetailNote(limited.DetailNote,
                otherSource + " 未找到相同番号；保留已核对的单站详情");
            return (limited, false);
        }
        catch (Exception ex)
        {
            Log(otherSource + " 手动聚合暂不可用：" + ex.Message);
            var limited = chosen.Clone();
            limited.DetailNote = JoinNativeDetailNote(limited.DetailNote,
                otherSource + " 本次不可用；该候选需人工审批，可稍后补全");
            return (limited, true);
        }
    }

    static string JoinNativeDetailNote(string? existing, string message) =>
        string.IsNullOrWhiteSpace(existing) ? message : existing.Trim() + "；" + message;
}
