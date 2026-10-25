using System.IO;
using System.Text;

namespace YinggeVideoLibrary;

internal static class SiteListImportDiagnostics
{
    internal static IReadOnlyDictionary<string, bool> Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "yingge-site-list-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var data = Path.Combine(root, "data");
            var csv = Path.Combine(root, "favorites.csv");
            File.WriteAllText(csv,
                "source,title,code,url\r\n" +
                "TMDB,Wrong display name,,https://www.themoviedb.org/movie/100\r\n" +
                "TMDB,Code title,ABC-123,\r\n" +
                "Bangumi,The Exact Title,,\r\n" +
                "Bangumi,The Exact Title,,\r\n" +
                "Kitsu,Ambiguous Title,,\r\n" +
                "Kitsu,Near Prefix Case,,\r\n" +
                "TVmaze,Unlisted Show,,\r\n" +
                "OtherSite,Conflicting identity,XYZ-888,https://www.themoviedb.org/movie/100\r\n" +
                "TMDB,\"Title, with comma\",,\r\n", new UTF8Encoding(false));
            var library = new[]
            {
                new SiteListImportStore.LocalVideo("v-url", "Local Movie", "Local Movie", "", "Local Movie", "", "https://www.themoviedb.org/movie/100"),
                new SiteListImportStore.LocalVideo("v-code", "Code Local", "Code Local", "ABC-123", "Code Local", "", ""),
                new SiteListImportStore.LocalVideo("v-exact", "The Exact Title", "The Exact Title", "", "", "", ""),
                new SiteListImportStore.LocalVideo("v-ambiguous-1", "Ambiguous Title", "Ambiguous Title", "", "", "", ""),
                new SiteListImportStore.LocalVideo("v-ambiguous-2", "Ambiguous Title", "Ambiguous Title", "", "", "", ""),
                new SiteListImportStore.LocalVideo("v-near", "Near Prefix Cinema", "Near Prefix Cinema", "", "", "", ""),
                new SiteListImportStore.LocalVideo("v-conflict", "Other Movie", "Other Movie", "XYZ-888", "", "", "")
            };
            var findings = new Dictionary<string, bool>(StringComparer.Ordinal);
            var canceledData = Path.Combine(root, "cancel-data");
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                var canceledWithoutWrite = false;
                try { SiteListImportStore.CreatePreview(canceledData, csv, library, canceled.Token); }
                catch (OperationCanceledException) { canceledWithoutWrite = true; }
                findings["siteListCanceledPreviewZeroWrite"] = canceledWithoutWrite &&
                    !File.Exists(Path.Combine(canceledData, "site-list-import.json"));
            }
            var preview = SiteListImportStore.CreatePreview(data, csv, library);
            findings["siteListPreviewIsReadOnly"] = !File.Exists(Path.Combine(data, "site-list-import.json"));
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                var commitCanceledWithoutWrite = false;
                try { SiteListImportStore.Commit(data, preview, library, canceled.Token); }
                catch (OperationCanceledException) { commitCanceledWithoutWrite = true; }
                findings["siteListCanceledCommitZeroWrite"] = commitCanceledWithoutWrite &&
                    !File.Exists(Path.Combine(data, "site-list-import.json"));
            }
            findings["siteListCsvStatuses"] = preview.Rows.Count == 9 && preview.Matched == 3 &&
                preview.Pending == 3 && preview.Unmatched == 2 && preview.Duplicates == 1 &&
                preview.Rows[0].VideoId == "v-url" && preview.Rows[1].VideoId == "v-code" &&
                preview.Rows[2].VideoId == "v-exact" && preview.Rows[3].Status == "重复" &&
                preview.Rows[7].Reason.Contains("不同本地视频", StringComparison.Ordinal) &&
                preview.Rows[8].Title == "Title, with comma";
            var changedIdentity = library.Select(video => video.VideoId == "v-url"
                ? video with { Size = 200 } : video).ToArray();
            var identityChangeRejected = false;
            try { SiteListImportStore.Commit(data, preview, changedIdentity); }
            catch (InvalidOperationException) { identityChangeRejected = true; }
            findings["siteListRescanIdentityChangeZeroWrite"] = identityChangeRejected &&
                !File.Exists(Path.Combine(data, "site-list-import.json"));
            var newlyAmbiguous = library.Append(new SiteListImportStore.LocalVideo("v-added",
                "The Exact Title", "The Exact Title", "", "", "", "")).ToArray();
            var ambiguityRejected = false;
            try { SiteListImportStore.Commit(data, preview, newlyAmbiguous); }
            catch (InvalidOperationException) { ambiguityRejected = true; }
            findings["siteListRescanAmbiguityZeroWrite"] = ambiguityRejected &&
                !File.Exists(Path.Combine(data, "site-list-import.json"));
            var committed = SiteListImportStore.Commit(data, preview, library);
            var stored = SiteListImportStore.Load(data);
            findings["siteListCommitPersistsAuditAndLinks"] = committed.EntryKeys.Count == 8 &&
                stored.Entries.Count == 8 && stored.Actions.Count == 1 &&
                stored.Entries.Count(entry => entry.Status == "已匹配") == 3 &&
                stored.Entries.Count(entry => entry.VideoId.Length > 0) == 3;
            var duplicatePreview = SiteListImportStore.CreatePreview(data, csv, library);
            findings["siteListRepeatImportIsIdempotent"] = duplicatePreview.Duplicates == 9 &&
                duplicatePreview.Importable == 0;
            var undo = SiteListImportStore.Undo(data, committed.BatchId);
            var afterUndo = SiteListImportStore.Load(data);
            findings["siteListUndoSurvivesReload"] = undo.Kind == "undo" && afterUndo.Entries.Count == 0 &&
                afterUndo.Actions.Count == 2 && afterUndo.Actions[1].BatchId == committed.BatchId;
            var repeatedUndoRejected = false;
            try { SiteListImportStore.Undo(data, committed.BatchId); }
            catch (InvalidOperationException) { repeatedUndoRejected = true; }
            findings["siteListRepeatedUndoRejected"] = repeatedUndoRejected && SiteListImportStore.Load(data).Revision == afterUndo.Revision;

            var json = Path.Combine(root, "favorites.json");
            File.WriteAllText(json, "{\"favorites\":[{\"source\":\"TVmaze\",\"title\":\"New Show\",\"url\":\"https://www.tvmaze.com/shows/3\"}]}",
                new UTF8Encoding(true));
            var jsonPreview = SiteListImportStore.CreatePreview(data, json, library);
            findings["siteListJsonUserExport"] = jsonPreview.Rows.Count == 1 &&
                jsonPreview.Rows[0].Title == "New Show" && jsonPreview.Unmatched == 1;
            File.AppendAllText(json, " ");
            var changedFileRejected = false;
            try { SiteListImportStore.Commit(data, jsonPreview, library); }
            catch (InvalidOperationException) { changedFileRejected = true; }
            findings["siteListChangedExportZeroWrite"] = changedFileRejected &&
                SiteListImportStore.Load(data).Revision == afterUndo.Revision;

            var json2 = Path.Combine(root, "other.json");
            File.WriteAllText(json2, "[{\"title\":\"Another Show\",\"source\":\"TVmaze\"}]", new UTF8Encoding(false));
            var oldPreview = SiteListImportStore.CreatePreview(data, json, library);
            var currentPreview = SiteListImportStore.CreatePreview(data, json2, library);
            SiteListImportStore.Commit(data, currentPreview, library);
            var staleRejected = false;
            try { SiteListImportStore.Commit(data, oldPreview, library); }
            catch (InvalidOperationException) { staleRejected = true; }
            findings["siteListConcurrentRevisionRejectsStalePreview"] = staleRejected &&
                SiteListImportStore.Load(data).Entries.Count == 1;

            var playbackData = Path.Combine(root, "playback-data");
            var media = Path.Combine(root, "same-path.mp4");
            File.WriteAllText(media, "original-file-content");
            var mediaInfo = new FileInfo(media);
            var originalVideo = new SiteListImportStore.LocalVideo("same-path-id", "Playback Work",
                "Playback Work", "", "Playback Work", "", "https://www.tvmaze.com/shows/123",
                media, mediaInfo.Length, mediaInfo.LastWriteTime.Ticks);
            var playbackExport = Path.Combine(root, "playback.json");
            File.WriteAllText(playbackExport,
                "[{\"title\":\"Playback Work\",\"url\":\"https://www.tvmaze.com/shows/123\"}]",
                new UTF8Encoding(false));
            var playbackPreview = SiteListImportStore.CreatePreview(playbackData, playbackExport, [originalVideo]);
            SiteListImportStore.Commit(playbackData, playbackPreview, [originalVideo]);
            var linked = SiteListImportStore.Load(playbackData).Entries.Single();
            findings["siteListUnchangedFileCanPlay"] = linked.LocalFingerprint.Length == 64 &&
                linked.ContentSha256.Length == 64 &&
                SiteListImportStore.CanPlayMatchedEntry(linked,
                    SiteListImportStore.RefreshCurrentFileStamp(originalVideo));
            var originalContent = File.ReadAllBytes(media);
            File.WriteAllBytes(media, Enumerable.Repeat((byte)'X', originalContent.Length).ToArray());
            File.SetLastWriteTime(media, new DateTime(originalVideo.ModifiedTicks, DateTimeKind.Local));
            var equalStampReplacement = SiteListImportStore.RefreshCurrentFileStamp(originalVideo);
            findings["siteListEqualStampReplacementCannotPlay"] = equalStampReplacement is not null &&
                equalStampReplacement.Size == originalVideo.Size &&
                equalStampReplacement.ModifiedTicks == originalVideo.ModifiedTicks &&
                !SiteListImportStore.CanPlayMatchedEntry(linked, equalStampReplacement);
            File.WriteAllBytes(media, originalContent);
            File.SetLastWriteTime(media, new DateTime(originalVideo.ModifiedTicks, DateTimeKind.Local));
            File.WriteAllText(media, "a different work at exactly the same path, with a different length");
            var replaced = SiteListImportStore.RefreshCurrentFileStamp(originalVideo);
            findings["siteListSamePathReplacementCannotPlay"] = replaced is not null &&
                replaced.VideoId == originalVideo.VideoId && replaced.Path == originalVideo.Path &&
                !SiteListImportStore.CanPlayMatchedEntry(linked, replaced);
            findings["siteListMetadataChangeCannotPlay"] = replaced is not null &&
                !SiteListImportStore.CanPlayMatchedEntry(linked,
                    originalVideo with { MetadataTitle = "Different Work" });
            var legacy = new SiteListImportStore.Entry { Status = "已匹配", VideoId = originalVideo.VideoId };
            findings["siteListLegacyMatchNeedsRecheckBeforePlay"] =
                !SiteListImportStore.CanPlayMatchedEntry(legacy, originalVideo);

            File.WriteAllText(Path.Combine(data, "site-list-import.json"), "{broken");
            var corruptRejected = false;
            try { SiteListImportStore.Load(data); }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException) { corruptRejected = true; }
            findings["siteListCorruptStateFailsClosed"] = corruptRejected;
            return findings;
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
