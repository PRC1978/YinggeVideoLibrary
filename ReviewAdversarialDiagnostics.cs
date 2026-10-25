using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace YinggeVideoLibrary;

// Runs only under --self-test-features. Every child is headless and receives
// an isolated directory beneath %TEMP%, never the user's active library.
internal static class ReviewAdversarialDiagnostics
{
    const string ProbePrefix = "YinggeReviewProcessProbe-";
    const string PendingName = "review-transaction-pending.json";

    internal static int RunChild(string[] args)
    {
        try
        {
            if (args.Length < 3 || !TryProbeRoot(args[2], out var root)) return 3;
            var dataDir = Path.GetFullPath(args[2]);
            switch (args[1])
            {
                case "recover" when args.Length == 4 && IsInside(root, args[3]):
                {
                    var completed = ReviewTransactionStore.RecoverPendingAtStartup(dataDir, out var error);
                    File.WriteAllText(args[3], error);
                    return completed ? 0 : 2;
                }
                case "crash-at" when args.Length == 5 && IsInside(root, args[4]):
                {
                    ReviewTransactionStore.RecoverPendingAtStartupForDiagnostics(dataDir, out _, step =>
                    {
                        if (step != args[3]) return;
                        File.WriteAllText(args[4], step);
                        // Parent kills this process after the durable write.
                        Thread.Sleep(TimeSpan.FromSeconds(30));
                    });
                    return 3;
                }
                case "hold-lease" when args.Length == 4 && IsInside(root, args[3]):
                {
                    Directory.CreateDirectory(dataDir);
                    using var lease = new FileStream(Path.Combine(dataDir, ".library-session.lock"),
                        FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    File.WriteAllText(args[3], "held");
                    Thread.Sleep(TimeSpan.FromSeconds(30));
                    return 3;
                }
                case "commit" when args.Length == 4 && IsInside(root, args[3]):
                {
                    var transaction = JsonSerializer.Deserialize<ReviewTransaction>(File.ReadAllText(args[3]))
                        ?? throw new InvalidDataException("测试审核事务为空。");
                    ReviewTransactionStore.Begin(dataDir, transaction);
                    return ReviewTransactionStore.RecoverPendingAtStartup(dataDir, out _) ? 0 : 2;
                }
                case "undo-last" when args.Length == 3:
                {
                    var history = ReviewTransactionStore.Load(dataDir, out var invalid);
                    var target = invalid == 0 ? ReviewTransactionStore.LastUndoable(history) : null;
                    if (target is null) return 2;
                    var undo = ReviewTransactionStore.Create("undo", target.After, target.Before,
                        undoesTransactionId: target.Id);
                    ReviewTransactionStore.Begin(dataDir, undo);
                    return ReviewTransactionStore.RecoverPendingAtStartup(dataDir, out _) ? 0 : 2;
                }
                default: return 3;
            }
        }
        catch (Exception ex)
        {
            try
            {
                if (args.Length >= 3 && TryProbeRoot(args[2], out var root))
                    File.WriteAllText(Path.Combine(root, "child-error-" + Guid.NewGuid().ToString("N") + ".txt"),
                        ex.ToString());
            }
            catch { }
            return 3;
        }
    }

    internal static async Task<IReadOnlyDictionary<string, bool>> RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), ProbePrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var results = new Dictionary<string, bool>(StringComparer.Ordinal);
        var children = new List<Process>();

        Process Start(string mode, string dataDir, params string[] arguments)
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
                throw new FileNotFoundException("审核进程测试无法找到当前程序。", executable);
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            start.ArgumentList.Add("--self-test-review-child");
            start.ArgumentList.Add(mode);
            start.ArgumentList.Add(dataDir);
            foreach (var arg in arguments) start.ArgumentList.Add(arg);
            var child = Process.Start(start) ?? throw new InvalidOperationException("无法启动审核隔离进程。");
            children.Add(child);
            return child;
        }

        static async Task<int> ExitAsync(Process child)
        {
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            return child.ExitCode;
        }

        static async Task ReadyAsync(Process child, string marker)
        {
            for (var i = 0; i < 400 && !File.Exists(marker); i++)
            {
                if (child.HasExited) throw new InvalidOperationException("审核子进程在写入就绪标记前退出：" + child.ExitCode);
                await Task.Delay(25);
            }
            if (!File.Exists(marker)) throw new TimeoutException("审核子进程就绪超时。");
        }

        async Task ProbeAsync(string key, Func<Task<bool>> run)
        {
            try { results[key] = await run(); }
            catch (Exception ex)
            {
                results[key] = false;
                File.AppendAllText(Path.Combine(root, "errors.txt"), key + Environment.NewLine + ex + Environment.NewLine);
            }
        }

        try
        {
            foreach (var step in new[] { "config", "history" })
            {
                await ProbeAsync("reviewCrossProcessCrashAfter" + char.ToUpperInvariant(step[0]) + step[1..], async () =>
                {
                    var data = Path.Combine(root, "crash-" + step);
                    var before = Pending("旧标题");
                    var after = Approved("新标题");
                    Seed(data, before);
                    var transaction = Create("field-approve", before, after);
                    ReviewTransactionStore.Begin(data, transaction);
                    var marker = Path.Combine(root, step + ".ready");
                    var paused = Start("crash-at", data, step, marker);
                    await ReadyAsync(paused, marker);
                    paused.Kill(entireProcessTree: true);
                    await ExitAsync(paused);
                    var result = Path.Combine(root, step + ".recovery-result");
                    var restarted = Start("recover", data, result);
                    var exit = await ExitAsync(restarted);
                    var history = ReviewTransactionStore.Load(data, out var invalid);
                    return exit == 0 && File.Exists(result) && File.ReadAllText(result).Length == 0 &&
                        invalid == 0 && history.Count(record => record.Id == transaction.Id) == 1 &&
                        !ReviewTransactionStore.HasPending(data) && SameSaved(data, after) && UnrelatedPreserved(data);
                });
            }

            await ProbeAsync("reviewCrossProcessLeaseContentionAndRecovery", async () =>
            {
                var data = Path.Combine(root, "lease-contention");
                var before = Pending("待审核");
                var after = Approved("通过");
                Seed(data, before);
                var transaction = Create("field-approve", before, after);
                ReviewTransactionStore.Begin(data, transaction);
                var marker = Path.Combine(root, "lease.ready");
                var holder = Start("hold-lease", data, marker);
                await ReadyAsync(holder, marker);
                var refusal = Path.Combine(root, "lease.refused");
                var contender = Start("recover", data, refusal);
                var blocked = await ExitAsync(contender) == 2 && File.Exists(refusal) &&
                    File.ReadAllText(refusal).Length > 0 && ReviewTransactionStore.HasPending(data) &&
                    SameSaved(data, before);
                holder.Kill(entireProcessTree: true);
                await ExitAsync(holder);
                var recovered = Path.Combine(root, "lease.recovered");
                var restarted = Start("recover", data, recovered);
                var history = ReviewTransactionStore.Load(data, out var invalidBefore);
                var exit = await ExitAsync(restarted);
                history = ReviewTransactionStore.Load(data, out var invalid);
                return blocked && invalidBefore == 0 && exit == 0 && invalid == 0 &&
                    history.Count(record => record.Id == transaction.Id) == 1 &&
                    !ReviewTransactionStore.HasPending(data) && SameSaved(data, after) && UnrelatedPreserved(data);
            });

            await ProbeAsync("reviewMalformedPendingNeverOverwritesLibrary", () =>
            {
                var allRejected = true;
                foreach (var variant in new[] { "truncated", "tampered-sha", "unknown-action", "incomplete", "wrong-ids" })
                {
                    var data = Path.Combine(root, "malformed-" + variant);
                    var before = Pending("原值");
                    var after = Approved("不得写入");
                    Seed(data, before);
                    var transaction = variant == "unknown-action"
                        ? Create("delete-file", before, after)
                        : variant == "incomplete"
                            ? ReviewTransactionStore.Create("field-approve",
                                new Dictionary<string, ReviewItemSnapshot> { ["one"] = before },
                                new Dictionary<string, ReviewItemSnapshot> { ["one"] = after }, complete: false)
                            : variant == "wrong-ids"
                                ? ReviewTransactionStore.Create("field-approve",
                                    new Dictionary<string, ReviewItemSnapshot> { ["one"] = before },
                                    new Dictionary<string, ReviewItemSnapshot> { ["two"] = after })
                                : Create("field-approve", before, after);
                    if (variant == "tampered-sha") transaction.After["one"].Metadata!.Title = "伪造标题";
                    File.WriteAllText(Path.Combine(data, PendingName), variant == "truncated"
                        ? "{\"SchemaVersion\":" : JsonSerializer.Serialize(transaction));
                    var rejected = !ReviewTransactionStore.RecoverPendingAtStartup(data, out var error);
                    var history = ReviewTransactionStore.Load(data, out var invalid);
                    allRejected &= rejected && error.Length > 0 && ReviewTransactionStore.HasPending(data) &&
                        invalid == 0 && history.Count == 0 && SameSaved(data, before) && UnrelatedPreserved(data);
                }
                return Task.FromResult(allRejected);
            });

            await ProbeAsync("reviewForgedHistoryIdFailsBeforePartialReplay", () =>
            {
                var data = Path.Combine(root, "forged-history-id");
                var before = Pending("原值");
                var after = Approved("合法目标");
                var forgedAfter = Approved("伪造目标");
                Seed(data, before);
                var transaction = Create("field-approve", before, after);
                ReviewTransactionStore.Begin(data, transaction);
                var forged = Create("reject", before, forgedAfter);
                forged.Id = transaction.Id;
                ReviewTransactionStore.Append(data, forged);
                var configBefore = File.ReadAllText(Path.Combine(data, "config.json"));
                var metadataBefore = File.ReadAllText(Path.Combine(data, "metadata.json"));
                var refused = !ReviewTransactionStore.RecoverPendingAtStartup(data, out var error);
                var history = ReviewTransactionStore.Load(data, out var invalid);
                return Task.FromResult(refused && error.Length > 0 && invalid == 0 && history.Count == 1 &&
                    ReviewTransactionStore.HasPending(data) &&
                    File.ReadAllText(Path.Combine(data, "config.json")) == configBefore &&
                    File.ReadAllText(Path.Combine(data, "metadata.json")) == metadataBefore &&
                    SameSaved(data, before) && UnrelatedPreserved(data));
            });

            await ProbeAsync("reviewThreeUndoAcrossSixFreshProcesses", async () =>
            {
                var data = Path.Combine(root, "three-undo-processes");
                var states = new[] { Pending("原始"), Approved("第一版"), Approved("第二版"), Approved("第三版") };
                states[2].Review!.Status = "rejected";
                Seed(data, states[0]);
                var commits = new[]
                {
                    Create("field-approve", states[0], states[1]),
                    Create("reject", states[1], states[2]),
                    Create("field-approve", states[2], states[3])
                };
                for (var i = 0; i < commits.Length; i++)
                {
                    var path = Path.Combine(root, "commit-" + i + ".json");
                    File.WriteAllText(path, JsonSerializer.Serialize(commits[i]));
                    if (await ExitAsync(Start("commit", data, path)) != 0 || !SameSaved(data, states[i + 1])) return false;
                }
                for (var i = 2; i >= 0; i--)
                {
                    if (ReviewTransactionStore.LastUndoable(ReviewTransactionStore.Load(data, out var invalid))?.Id != commits[i].Id ||
                        invalid != 0 || await ExitAsync(Start("undo-last", data)) != 0 ||
                        !SameSaved(data, states[i])) return false;
                }
                var history = ReviewTransactionStore.Load(data, out var invalidFinal);
                return invalidFinal == 0 && history.Count == 6 &&
                    ReviewTransactionStore.LastUndoable(history) is null &&
                    !ReviewTransactionStore.HasPending(data) && SameSaved(data, states[0]) && UnrelatedPreserved(data);
            });
        }
        finally
        {
            foreach (var child in children)
            {
                try
                {
                    if (!child.HasExited) child.Kill(entireProcessTree: true);
                    child.WaitForExit(5000);
                }
                catch { }
                child.Dispose();
            }
            if (results.Count == 6 && results.Values.All(value => value))
                try { Directory.Delete(root, recursive: true); } catch { }
        }
        return results;
    }

    static bool TryProbeRoot(string dataDir, out string root)
    {
        root = "";
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(dataDir);
        if (!full.StartsWith(temp, StringComparison.OrdinalIgnoreCase)) return false;
        var top = Path.GetRelativePath(temp, full)
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        if (top is null || !top.StartsWith(ProbePrefix, StringComparison.Ordinal) || top.Contains("..", StringComparison.Ordinal))
            return false;
        root = Path.Combine(temp, top);
        return IsInside(root, full);
    }

    static bool IsInside(string root, string path)
    {
        var full = Path.GetFullPath(path);
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    static ReviewItemSnapshot Pending(string title) => new()
    {
        Review = new AiReviewRecord { VideoId = "one", CandidateTitle = title, Status = "pending", Confidence = 55 },
        Metadata = new MovieMetadata { Title = title }, Tags = ["原标签"]
    };

    static ReviewItemSnapshot Approved(string title) => new()
    {
        Review = new AiReviewRecord { VideoId = "one", CandidateTitle = title, Status = "approved", Confidence = 95 },
        Metadata = new MovieMetadata { Title = title, DetailFetched = true }, Tags = ["新标签"]
    };

    static ReviewTransaction Create(string action, ReviewItemSnapshot before, ReviewItemSnapshot after)
        => ReviewTransactionStore.Create(action,
            new Dictionary<string, ReviewItemSnapshot> { ["one"] = before },
            new Dictionary<string, ReviewItemSnapshot> { ["one"] = after });

    static void Seed(string dataDir, ReviewItemSnapshot state)
    {
        Directory.CreateDirectory(dataDir);
        var config = new LibraryConfig();
        if (state.Review is not null) config.AiReviews["one"] = state.Review;
        if (state.Tags is not null) config.Tags["one"] = state.Tags;
        File.WriteAllText(Path.Combine(dataDir, "config.json"), JsonSerializer.Serialize(config));
        var metadata = new Dictionary<string, MovieMetadata>
        {
            ["one"] = state.Metadata!, ["unrelated"] = new() { Title = "其它视频保留" }
        };
        File.WriteAllText(Path.Combine(dataDir, "metadata.json"), JsonSerializer.Serialize(metadata));
    }

    static bool SameSaved(string dataDir, ReviewItemSnapshot expected)
    {
        var config = JsonSerializer.Deserialize<LibraryConfig>(File.ReadAllText(Path.Combine(dataDir, "config.json")))!;
        var metadata = MetadataStore.Load(dataDir);
        var actual = new ReviewItemSnapshot
        {
            Review = config.AiReviews.GetValueOrDefault("one"),
            Metadata = metadata.GetValueOrDefault("one"),
            Tags = config.Tags.GetValueOrDefault("one"),
            AutoAmateur = config.AutoAmateurTags.Contains("one", StringComparer.OrdinalIgnoreCase)
        };
        return ReviewTransactionStore.Equal(actual, expected);
    }

    static bool UnrelatedPreserved(string dataDir)
        => MetadataStore.Load(dataDir).GetValueOrDefault("unrelated")?.Title == "其它视频保留";
}
