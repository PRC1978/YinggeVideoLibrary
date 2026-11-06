using System.IO;

namespace YinggeVideoLibrary;

internal static class PostgresTitleCandidateStore
{
    internal static async Task<ScrapeCandidateCache.TitleCandidateSnapshot?> LoadAsync(
        string connectionString, string videoId)
    {
        if (string.IsNullOrWhiteSpace(videoId)) return null;
        var name = ScrapeCandidateCache.TitleSnapshotDocumentName(videoId);
        var document = await PostgresLibraryDocumentStore.ReadAsync(connectionString, name);
        if (document is null) return null;
        return ScrapeCandidateCache.ParseTitleSnapshot(document.Content, videoId)
            ?? throw new InvalidDataException("数据库标题候选格式无效；原记录已保留。");
    }

    internal static async Task SaveAsync(string connectionString,
        ScrapeCandidateCache.TitleCandidateSnapshot snapshot)
    {
        var name = ScrapeCandidateCache.TitleSnapshotDocumentName(snapshot.VideoId);
        var bytes = ScrapeCandidateCache.SerializeTitleSnapshot(snapshot);
        var baseline = await PostgresLibraryDocumentStore.ReadAsync(connectionString, name);
        await PostgresLibraryDocumentStore.SaveAsync(connectionString, name, bytes, baseline?.Revision);
    }
}
