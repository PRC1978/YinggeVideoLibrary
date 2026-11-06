namespace YinggeVideoLibrary;

public partial class MainWindow
{
    ScrapeCandidateCache.TitleCandidateSnapshot? LoadSavedTitleCandidates(string videoId)
    {
        if (postgresSession is null) return ScrapeCandidateCache.LoadTitleCandidates(DataDir, videoId);
        return Task.Run(() => PostgresTitleCandidateStore.LoadAsync(
            postgresSession.ConnectionString, videoId)).GetAwaiter().GetResult();
    }

    void SaveTitleCandidates(ScrapeCandidateCache.TitleCandidateSnapshot snapshot)
    {
        if (postgresSession is null)
        {
            ScrapeCandidateCache.SaveTitleCandidates(DataDir, snapshot);
            return;
        }
        Task.Run(() => PostgresTitleCandidateStore.SaveAsync(
            postgresSession.ConnectionString, snapshot)).GetAwaiter().GetResult();
    }
}
