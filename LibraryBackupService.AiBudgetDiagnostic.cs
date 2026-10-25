using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace YinggeVideoLibrary;

internal static partial class LibraryBackupService
{
    internal static IReadOnlyDictionary<string, bool> VerifyAiPacingRestoreForDiagnostics(string root)
    {
        var live = Path.Combine(root, "live");
        var backup = Path.Combine(root, "backup");
        Directory.CreateDirectory(live);
        Directory.CreateDirectory(backup);
        var day = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var now = DateTimeOffset.UtcNow;
        var localCurrent = now.AddMinutes(30);
        var cloudCurrent = now.AddMinutes(10);
        var localSaved = now.AddMinutes(5);
        var cloudSaved = now.AddMinutes(40);
        var livePath = Path.Combine(live, "ai-budget.json");
        var backupPath = Path.Combine(backup, "ai-budget.json");
        File.WriteAllText(livePath, JsonSerializer.Serialize(new
        {
            Day = day, EstimatedUsd = 0.6, NextLocalAllowedUtc = localCurrent,
            NextCloudAllowedUtc = cloudCurrent
        }));
        File.WriteAllText(backupPath, JsonSerializer.Serialize(new
        {
            Day = day, EstimatedUsd = 0.5, NextLocalAllowedUtc = localSaved,
            NextCloudAllowedUtc = cloudSaved
        }));
        RestoreQuotaLedger("ai-budget.json", live, backup);
        var merged = JsonNode.Parse(File.ReadAllText(livePath))!.AsObject();
        var first = Math.Abs((merged["EstimatedUsd"]?.GetValue<double>() ?? 0) - 0.6) < 0.000000001d &&
            ReadNextAllowed(merged, "NextLocalAllowedUtc") >= localCurrent &&
            ReadNextAllowed(merged, "NextCloudAllowedUtc") >= cloudSaved;

        var laterLocal = now.AddHours(1);
        File.WriteAllText(backupPath, JsonSerializer.Serialize(new
        {
            Day = day, EstimatedUsd = 0.9, NextLocalAllowedUtc = laterLocal,
            NextCloudAllowedUtc = now.AddMinutes(15)
        }));
        RestoreQuotaLedger("ai-budget.json", live, backup);
        var preferred = JsonNode.Parse(File.ReadAllText(livePath))!.AsObject();
        var second = Math.Abs((preferred["EstimatedUsd"]?.GetValue<double>() ?? 0) - 0.9) < 0.000000001d &&
            ReadNextAllowed(preferred, "NextLocalAllowedUtc") >= laterLocal &&
            ReadNextAllowed(preferred, "NextCloudAllowedUtc") >= cloudSaved;

        using var invalid = JsonDocument.Parse("{\"Day\":\"" + day + "\",\"EstimatedUsd\":0.2,\"NextLocalAllowedUtc\":5}");
        using var legacy = JsonDocument.Parse("{\"Day\":\"" + day + "\",\"EstimatedUsd\":0.2}");
        return new Dictionary<string, bool>
        {
            ["aiBackupMergeKeepsBothChannelPacingClocks"] = first && second,
            ["aiBackupNewClockSchemaRejectsInvalidKeepsLegacy"] =
                !ValidQuotaSnapshot("ai-budget.json", invalid.RootElement) &&
                ValidQuotaSnapshot("ai-budget.json", legacy.RootElement)
        };
    }
}
