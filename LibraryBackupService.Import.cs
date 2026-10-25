using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace YinggeVideoLibrary;

[Flags]
internal enum LibraryImportScope
{
    None = 0,
    Metadata = 1,
    Categories = 2,
    Tags = 4,
    Ratings = 8,
    Reviews = 16,
    Queue = 32,
    SiteLists = 64,
    RoleProfiles = 128,
    All = Metadata | Categories | Tags | Ratings | Reviews | Queue | SiteLists | RoleProfiles
}

internal enum LibraryImportConflictPolicy
{
    KeepCurrent = 0,
    PreferBackup = 1
}

internal static partial class LibraryBackupService
{
    const string PendingImportName = "import-pending.json";
    const string ActiveImportName = "import-active.json";

    internal sealed class ImportScopeSummary
    {
        public required LibraryImportScope Scope { get; init; }
        public required string Label { get; init; }
        public int BackupItems { get; set; }
        public int CurrentItems { get; set; }
        public int Added { get; set; }
        public int Replaced { get; set; }
        public int KeptConflicts { get; set; }
    }

    // Only the selected tag and personal-rating values are shown in a
    // conflict preview. This never includes credentials or AI request data.
    internal sealed record ImportConflictDetail(LibraryImportScope Scope, string VideoId,
        string CurrentValue, string BackupValue);

    internal sealed class ImportPreview
    {
        public required string BackupPath { get; init; }
        public required string DestinationPath { get; init; }
        public required DateTimeOffset CreatedUtc { get; init; }
        public required LibraryImportScope Scopes { get; init; }
        public required LibraryImportConflictPolicy ConflictPolicy { get; init; }
        public required string BackupManifestSha256 { get; init; }
        public required string LiveStateSha256 { get; init; }
        public required IReadOnlyList<ImportScopeSummary> Sections { get; init; }
        public required IReadOnlyList<ImportConflictDetail> ConflictDetails { get; init; }
        public required IReadOnlyList<CrossDeviceMatch> MappingRows { get; init; }
        public required IReadOnlyList<string> UnindexedCategoryIds { get; init; }
        public required bool SkipUnindexedCategories { get; init; }
        public required IReadOnlyDictionary<string, string> ManualChoices { get; init; }
        public required string MappingDigest { get; init; }
        public bool MappingComplete => MappingRows.All(row => row.Status is "唯一内容匹配" or "人工选择的相同内容")
            && (UnindexedCategoryIds.Count == 0 || SkipUnindexedCategories);
        public bool HasChanges => MappingComplete && Sections.Any(section => section.Added > 0 || section.Replaced > 0);

        public string ConfirmationText =>
            "导入来源：" + BackupPath + "\n"
            + "备份时间：" + CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + "\n"
            + "导入目标：" + DestinationPath + "\n"
            + "冲突策略：" + (ConflictPolicy == LibraryImportConflictPolicy.KeepCurrent
                ? "保留当前资料（默认）" : "优先使用备份资料") + "\n\n"
            + string.Join("\n", Sections.Select(section =>
                $"{section.Label}：备份 {section.BackupItems}，当前 {section.CurrentItems}，新增 {section.Added}，"
                + $"替换 {section.Replaced}，保留冲突 {section.KeptConflicts}"))
            + (ConflictDetails.Count == 0 ? "" : "\n\n标签/评分冲突（最多显示前 20 项，双方原文件仍保留）：\n"
                + string.Join("\n", ConflictDetails.Select(detail =>
                    $"{(detail.Scope == LibraryImportScope.Tags ? "标签" : "评分")} {PreviewText(detail.VideoId, 48)}："
                    + $"当前 {PreviewText(detail.CurrentValue, 160)}；备份 {PreviewText(detail.BackupValue, 160)}")))
            + (MappingRows.Count == 0 ? "" : "\n\n跨设备内容匹配："
                + $"{MappingRows.Count(row => row.Status is "唯一内容匹配" or "人工选择的相同内容")}/{MappingRows.Count} 部已确认。\n"
                + string.Join("\n", MappingRows.Where(row => row.Status != "唯一内容匹配")
                    .Take(20).Select(row => PreviewText(row.RelativePath, 120) + "：" + row.Status
                        + (row.ExactCandidates.Count > 1 ? $"（{row.ExactCandidates.Count} 个候选）" : ""))))
            + (UnindexedCategoryIds.Count == 0 ? "" : "\n\n来源索引外历史分类："
                + UnindexedCategoryIds.Count.ToString("N0") + " 个旧视频编号。"
                + (SkipUnindexedCategories ? " 已明确选择仅导入索引内分类；来源包保持完整。"
                    : " 默认禁止导入；可选择跳过这些旧分类后重新预览。")
                + "\n示例：" + string.Join("、", UnindexedCategoryIds.Take(12))) + "\n\n"
            + "仅处理勾选范围；其他设置、Windows 凭据、视频原文件和用量账本不会被导入覆盖。\n"
            + "审核历史只追加新记录，同一审核 ID 的现有记录始终保留。\n"
            + "站点片单按完整导入批次合并；重复键或审计冲突始终保留当前批次。\n"
            + "确认后于下次启动前导入；资料或备份变化时会停止，且可在启动前取消。";

        static string PreviewText(string value, int maxLength)
        {
            var line = value.Replace('\r', ' ').Replace('\n', ' ');
            return line.Length <= maxLength ? line : line[..maxLength] + "…";
        }
    }

    sealed class ImportRequest
    {
        public string BackupPath { get; set; } = "";
        public string RollbackPath { get; set; } = "";
        public string BackupManifestSha256 { get; set; } = "";
        public string LiveStateSha256 { get; set; } = "";
        public string MappingDigest { get; set; } = "";
        public bool SkipUnindexedCategories { get; set; }
        public Dictionary<string, string> ManualChoices { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public LibraryImportScope Scopes { get; set; }
        public LibraryImportConflictPolicy ConflictPolicy { get; set; }
    }

    sealed class ImportPlan
    {
        public Dictionary<string, string> FileContents { get; } = new(StringComparer.Ordinal);
        public List<ImportScopeSummary> Sections { get; } = [];
        public List<ImportConflictDetail> ConflictDetails { get; } = [];
        public bool QueueChanged => FileContents.ContainsKey("scrape-queue.json");
    }

    static string PendingImportPath(string dataDir) => Path.Combine(dataDir, PendingImportName);
    static string ActiveImportPath(string dataDir) => Path.Combine(dataDir, ActiveImportName);

    internal static bool HasPendingImport(string dataDir) => File.Exists(PendingImportPath(dataDir));

    internal static void CancelPendingImport(string dataDir)
    {
        if (File.Exists(ActiveImportPath(dataDir)))
            throw new InvalidOperationException("离线导入正在恢复上次中断的操作，不能取消回滚。");
        File.Delete(PendingImportPath(dataDir));
    }

    internal static ImportPreview CreateImportPreview(string dataDir, string backupDirectory,
        LibraryImportScope scopes, LibraryImportConflictPolicy policy = LibraryImportConflictPolicy.KeepCurrent,
        IReadOnlyDictionary<string, string>? manualChoices = null, bool skipUnindexedCategories = false)
    {
        ValidateImportOptions(scopes, policy);
        var destination = Path.GetFullPath(dataDir);
        var source = Path.GetFullPath(backupDirectory);
        RejectReparseAncestors(destination);
        RejectReparseAncestors(source);
        var check = Verify(source);
        if (!check.IsValid) throw new InvalidDataException("导入包校验失败：" + check.Message);
        var manifestPath = Path.Combine(source, ManifestName);
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath))
            ?? throw new InvalidDataException("导入包清单无法读取。");
        var crossDevice = RequiresCrossDeviceMapping(destination, source, manifest);
        var mapping = crossDevice ? ResolveCrossDeviceMap(destination, manifest, manualChoices) : null;
        IReadOnlyList<string> orphans = mapping is null ? [] : SourceUnindexedCategoryIds(source, scopes, manifest);
        if (mapping?.Complete == true)
            orphans = ValidateCrossDeviceCoveredIds(source, scopes, mapping, manifest);
        var ready = mapping is null || (mapping.Complete && (orphans.Count == 0 || skipUnindexedCategories));
        using var mapped = mapping is not null && ready
            ? new MappedImportSource(source, destination, mapping, scopes,
                skipUnindexedCategories ? orphans : []) : null;
        var plan = ready
            ? BuildImportPlan(destination, mapped?.Path ?? source, scopes, policy)
            : new ImportPlan();
        var rows = mapping?.Rows ?? [];
        return new ImportPreview
        {
            BackupPath = source, DestinationPath = destination, CreatedUtc = manifest.CreatedUtc,
            Scopes = scopes, ConflictPolicy = policy, BackupManifestSha256 = HashFile(manifestPath),
            LiveStateSha256 = SnapshotLiveState(destination), Sections = plan.Sections,
            ConflictDetails = plan.ConflictDetails, MappingRows = rows,
            UnindexedCategoryIds = orphans, SkipUnindexedCategories = skipUnindexedCategories,
            ManualChoices = new Dictionary<string, string>(manualChoices ?? new Dictionary<string, string>(),
                StringComparer.OrdinalIgnoreCase), MappingDigest = MappingDigest(rows)
        };
    }

    internal static void StageImport(string dataDir, ImportPreview preview)
    {
        if (File.Exists(PendingPath(dataDir)) || File.Exists(ActivePath(dataDir))
            || File.Exists(PendingOfflineBackupPath(dataDir)) || File.Exists(ActiveImportPath(dataDir))
            || DuplicateMetadataTransactionStore.HasPending(dataDir))
            throw new InvalidOperationException("已有备份或恢复任务，不能同时排定导入。");
        if (!preview.MappingComplete)
            throw new InvalidOperationException("跨设备视频或来源索引外历史分类尚未处理，请重新预览确认。");
        var fresh = CreateImportPreview(dataDir, preview.BackupPath, preview.Scopes,
            preview.ConflictPolicy, preview.ManualChoices, preview.SkipUnindexedCategories);
        if (!string.Equals(fresh.DestinationPath, preview.DestinationPath, StringComparison.OrdinalIgnoreCase)
            || fresh.BackupManifestSha256 != preview.BackupManifestSha256
            || fresh.LiveStateSha256 != preview.LiveStateSha256
            || fresh.MappingDigest != preview.MappingDigest)
            throw new InvalidOperationException("备份或当前资料在预览后发生变化，请重新预览后再导入。");
        var pending = PendingImportPath(dataDir);
        var temporary = pending + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new ImportRequest
        {
            BackupPath = fresh.BackupPath, BackupManifestSha256 = fresh.BackupManifestSha256,
            LiveStateSha256 = fresh.LiveStateSha256, Scopes = fresh.Scopes,
            ConflictPolicy = fresh.ConflictPolicy, MappingDigest = fresh.MappingDigest,
            SkipUnindexedCategories = fresh.SkipUnindexedCategories,
            ManualChoices = new Dictionary<string, string>(fresh.ManualChoices, StringComparer.OrdinalIgnoreCase)
        }));
        File.Move(temporary, pending, true);
    }

    internal static bool ApplyPendingImportAtStartup(string dataDir, out string error,
        bool requireSingleInstance = true)
    {
        error = "";
        var pendingPath = PendingImportPath(dataDir);
        var activePath = ActiveImportPath(dataDir);
        if (!File.Exists(pendingPath) && !File.Exists(activePath)) return true;
        var exclusiveLeaseAcquired = false;
        try
        {
            Directory.CreateDirectory(dataDir);
            using var gate = new FileStream(Path.Combine(dataDir, SessionLeaseName), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
            exclusiveLeaseAcquired = true;
            if (requireSingleInstance && LegacyInstanceLikelyRunning())
            {
                error = "另一份映格正在使用资料库；请关闭所有窗口后再导入。";
                return false;
            }
            if (File.Exists(activePath))
            {
                var interrupted = ReadImportRequest(activePath);
                RestoreImportRollback(dataDir, interrupted);
                File.Delete(activePath);
                File.Delete(pendingPath);
                error = "上次导入中断，已恢复原数据。请重新预览。";
                return false;
            }
            var request = ReadImportRequest(pendingPath);
            ValidateImportOptions(request.Scopes, request.ConflictPolicy);
            var source = Path.GetFullPath(request.BackupPath);
            RejectReparseAncestors(source);
            var check = Verify(source);
            if (!check.IsValid || request.BackupManifestSha256.Length != 64
                || !HashFile(Path.Combine(source, ManifestName)).Equals(request.BackupManifestSha256,
                    StringComparison.OrdinalIgnoreCase)
                || request.LiveStateSha256.Length != 64
                || !SnapshotLiveState(dataDir).Equals(request.LiveStateSha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(pendingPath);
                throw new InvalidDataException("导入包或当前资料在确认后发生变化；已取消导入，当前数据未修改。请重新预览。");
            }
            var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(Path.Combine(source, ManifestName)))
                ?? throw new InvalidDataException("导入包清单无法读取。");
            var mapping = RequiresCrossDeviceMapping(dataDir, source, manifest)
                ? ResolveCrossDeviceMap(dataDir, manifest, request.ManualChoices) : null;
            if (mapping is { Complete: false } || MappingDigest(mapping?.Rows ?? []) != request.MappingDigest)
                throw new InvalidDataException("视频内容或人工映射在确认后发生变化；当前资料未修改，请重新预览。");
            IReadOnlyList<string> orphans = mapping is null ? []
                : ValidateCrossDeviceCoveredIds(source, request.Scopes, mapping, manifest);
            if (orphans.Count > 0 && !request.SkipUnindexedCategories)
                throw new InvalidDataException("来源存在索引外历史分类，尚未明确选择跳过；当前资料未修改。");
            using var mapped = mapping is not null
                ? new MappedImportSource(source, dataDir, mapping, request.Scopes,
                    request.SkipUnindexedCategories ? orphans : []) : null;
            var plan = BuildImportPlan(dataDir, mapped?.Path ?? source, request.Scopes, request.ConflictPolicy);
            if (plan.FileContents.Count == 0)
            {
                File.Delete(pendingPath);
                return true;
            }
            var rollback = Create(dataDir, rollback: true);
            var activeTemporary = activePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            request.RollbackPath = rollback;
            File.WriteAllText(activeTemporary, JsonSerializer.Serialize(request));
            File.Move(activeTemporary, activePath, true);
            ApplyImportPlan(dataDir, plan);
            File.Delete(pendingPath);
            File.Delete(activePath);
            return true;
        }
        catch (Exception ex)
        {
            error = ex is IOException && !exclusiveLeaseAcquired
                ? "另一份映格正在使用资料库；离线导入保留待办，请关闭所有窗口后重启。"
                : ex.Message;
            if (File.Exists(activePath))
            {
                try
                {
                    RestoreImportRollback(dataDir, ReadImportRequest(activePath));
                    File.Delete(activePath);
                    File.Delete(pendingPath);
                    error += " 已回滚此次导入。";
                }
                catch (Exception rollbackError)
                {
                    error += " 自动回滚未完成，下次启动会重试：" + rollbackError.Message;
                }
            }
            else if (exclusiveLeaseAcquired)
            {
                // No managed file has been written without the active marker.
                // Discard an invalid/stale request so the next ordinary launch
                // can load the library; a fresh preview is required to retry.
                try { File.Delete(pendingPath); }
                catch (Exception cleanupError) { error += " 无法取消待办：" + cleanupError.Message; }
            }
            return false;
        }
    }

    static void RestoreImportRollback(string dataDir, ImportRequest request)
    {
        var rollbackPath = Path.GetFullPath(request.RollbackPath);
        var expectedRoot = Path.GetFullPath(DefaultBackupRoot(dataDir));
        if (!string.Equals(Path.GetDirectoryName(rollbackPath), expectedRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("导入回滚包不在本机回滚目录。");
        RejectReparseAncestors(rollbackPath);
        var rollback = Verify(rollbackPath, allowRawRollback: true);
        if (!rollback.IsValid) throw new InvalidDataException("导入回滚包损坏：" + rollback.Message);
        RestoreFiles(dataDir, rollbackPath, rawRollback: true);
    }

    static ImportRequest ReadImportRequest(string path) =>
        JsonSerializer.Deserialize<ImportRequest>(File.ReadAllText(path))
        ?? throw new InvalidDataException("离线导入请求损坏。");

    static void ValidateImportOptions(LibraryImportScope scopes, LibraryImportConflictPolicy policy)
    {
        if (scopes == LibraryImportScope.None || (scopes & ~LibraryImportScope.All) != 0)
            throw new ArgumentOutOfRangeException(nameof(scopes), "请选择有效的数据范围。");
        if (policy is not (LibraryImportConflictPolicy.KeepCurrent or LibraryImportConflictPolicy.PreferBackup))
            throw new ArgumentOutOfRangeException(nameof(policy), "冲突策略无效。");
    }

    static void ApplyImportPlan(string dataDir, ImportPlan plan)
    {
        RejectReparseAncestors(dataDir);
        foreach (var (name, contents) in plan.FileContents.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            var destination = Path.Combine(dataDir, name);
            if (File.Exists(destination) && (File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("目标文件是链接，已停止导入：" + name);
            var temporary = destination + ".import-" + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, contents, Encoding.UTF8);
            if (name == "scrape-queue.json")
            {
                foreach (var sidecar in new[] { "scrape-queue.state.json", "scrape-queue.state.json.bak", "scrape-queue.json.bak" })
                    File.Delete(Path.Combine(dataDir, sidecar));
                foreach (var pattern in new[] { "scrape-queue.snapshot.*.json", "scrape-queue.journal.*.bin" })
                    foreach (var path in Directory.EnumerateFiles(dataDir, pattern, SearchOption.TopDirectoryOnly)) File.Delete(path);
            }
            if (name is RoleProfileStore.FileName or "site-list-import.json")
                File.Delete(destination + ".bak");
            File.Move(temporary, destination, true);
        }
        if (plan.QueueChanged) ScrapeQueueStore.ForgetCachedStateAfterOfflineRestore(dataDir);
    }
}
