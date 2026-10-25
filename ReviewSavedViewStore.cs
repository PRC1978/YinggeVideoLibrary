using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace YinggeVideoLibrary;

internal sealed class ReviewSavedView
{
    public string Name { get; set; } = "";
    public string Status { get; set; } = "all";
    public string Source { get; set; } = "";
    public string Confidence { get; set; } = "all";

    public ReviewSavedView Clone() => new()
    {
        Name = Name, Status = Status, Source = Source, Confidence = Confidence
    };
}

internal static class ReviewSavedViewStore
{
    const string FileName = "review-views.json";
    const int SchemaVersion = 1;
    const int MaxViews = 32;
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    sealed class Document
    {
        public int Version { get; set; } = SchemaVersion;
        public List<ReviewSavedView> Views { get; set; } = [];
    }

    public static IReadOnlyList<ReviewSavedView> Load(string dataDir, out bool recovered)
    {
        var document = ReadDocument(dataDir, out recovered);
        return document.Views.Select(view => view.Clone()).ToList();
    }

    public static IReadOnlyList<ReviewSavedView> Upsert(string dataDir, ReviewSavedView view)
    {
        var clean = Validate(view);
        return Mutate(dataDir, document =>
        {
            var index = document.Views.FindIndex(item => string.Equals(item.Name, clean.Name, StringComparison.CurrentCultureIgnoreCase));
            if (index >= 0) document.Views[index] = clean;
            else
            {
                if (document.Views.Count >= MaxViews) throw new InvalidDataException($"最多保存 {MaxViews} 个审核视图。");
                document.Views.Add(clean);
            }
        });
    }

    public static IReadOnlyList<ReviewSavedView> Delete(string dataDir, string name) =>
        Mutate(dataDir, document => document.Views.RemoveAll(item => string.Equals(item.Name, name, StringComparison.CurrentCultureIgnoreCase)));

    static IReadOnlyList<ReviewSavedView> Mutate(string dataDir, Action<Document> update)
    {
        Directory.CreateDirectory(dataDir);
        var normalized = Path.GetFullPath(dataDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();
        var mutexName = "Local\\YinggeReviewViews." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..24];
        using var mutex = new Mutex(false, mutexName);
        try { mutex.WaitOne(); }
        catch (AbandonedMutexException) { /* Re-read the last valid document after an interrupted writer. */ }
        try
        {
            var document = ReadDocument(dataDir, out _);
            update(document);
            document.Views = document.Views.OrderBy(view => view.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            WriteDocument(dataDir, document);
            return document.Views.Select(view => view.Clone()).ToList();
        }
        finally { mutex.ReleaseMutex(); }
    }

    static Document ReadDocument(string dataDir, out bool recovered)
    {
        var path = Path.Combine(dataDir, FileName);
        recovered = false;
        if (TryRead(path, out var document)) return document;
        var backup = path + ".bak";
        if (TryRead(backup, out document))
        {
            recovered = true;
            return document;
        }
        if (File.Exists(path) || File.Exists(backup))
            throw new InvalidDataException("保存的审核视图及恢复副本都无法读取；已停止覆盖，原文件仍保留。");
        return new Document();
    }

    static bool TryRead(string path, out Document document)
    {
        document = new Document();
        if (!File.Exists(path)) return false;
        try
        {
            if (new FileInfo(path).Length > 64 * 1024) return false;
            var read = JsonSerializer.Deserialize<Document>(File.ReadAllText(path));
            if (read?.Version != SchemaVersion || read.Views is null || read.Views.Count > MaxViews) return false;
            var seen = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
            foreach (var view in read.Views)
            {
                var clean = Validate(view);
                if (!seen.Add(clean.Name)) return false;
                view.Name = clean.Name;
                view.Source = clean.Source;
            }
            document = read;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
        { return false; }
    }

    static ReviewSavedView Validate(ReviewSavedView? view)
    {
        if (view is null) throw new InvalidDataException("审核视图为空。");
        var name = view.Name?.Trim() ?? "";
        var source = view.Source?.Trim() ?? "";
        if (name.Length is < 1 or > 40 || name.Any(char.IsControl) ||
            source.Length > 160 || source.Any(char.IsControl) ||
            view.Status is not ("all" or "pending" or "rejected" or "approved" or "unmatched") ||
            view.Confidence is not ("all" or "low" or "medium" or "high"))
            throw new InvalidDataException("审核视图名称或筛选条件无效。");
        return new ReviewSavedView { Name = name, Status = view.Status, Source = source, Confidence = view.Confidence };
    }

    static void WriteDocument(string dataDir, Document document)
    {
        var path = Path.Combine(dataDir, FileName);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var backupTemporary = path + "." + Guid.NewGuid().ToString("N") + ".bak.tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, document, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
            try
            {
                File.Copy(path, backupTemporary);
                File.Move(backupTemporary, path + ".bak", overwrite: true);
            }
            catch (IOException) { /* The primary was committed; a previous valid backup remains. */ }
            catch (UnauthorizedAccessException) { /* Same as above. */ }
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            try { if (File.Exists(backupTemporary)) File.Delete(backupTemporary); } catch { }
        }
    }

    internal static bool VerifyForDiagnostics()
    {
        var root = Path.Combine(Path.GetTempPath(), "yingge-review-views-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Upsert(root, new ReviewSavedView { Name = "低可信待审", Status = "pending", Source = "JavDB", Confidence = "low" });
            Upsert(root, new ReviewSavedView { Name = "人工否决", Status = "rejected", Source = "Pornhub", Confidence = "high" });
            var reopened = Load(root, out var recovered);
            if (recovered || reopened.Count != 2 || reopened.Single(view => view.Name == "低可信待审").Source != "JavDB") return false;
            File.WriteAllText(Path.Combine(root, FileName), "{interrupted");
            var fallback = Load(root, out recovered);
            if (!recovered || fallback.Count != 2) return false;
            Upsert(root, new ReviewSavedView { Name = "低可信待审", Status = "pending", Source = "JavDB", Confidence = "medium" });
            if (Load(root, out _).Single(view => view.Name == "低可信待审").Confidence != "medium") return false;
            Delete(root, "人工否决");
            if (Load(root, out _).Count != 1) return false;
            try { Upsert(root, new ReviewSavedView { Name = "坏\n名称", Confidence = "low" }); return false; }
            catch (InvalidDataException) { }
            return Load(root, out _).Single().Name == "低可信待审";
        }
        finally
        {
            foreach (var path in Directory.EnumerateFiles(root)) File.Delete(path);
            Directory.Delete(root);
        }
    }
}
