using System.IO;
using System.Text.Json;

namespace YinggeVideoLibrary;

internal static class AiCostPreviewDiagnostics
{
    internal static int RunToFile(string path)
    {
        var config = new LibraryConfig
        {
            AiCloudRetryCount = 2,
            AiLocalRetryCount = 1,
            AiInputUsdPerMillion = 2,
            AiOutputUsdPerMillion = 8,
            AiMaxOutputTokens = 256,
            AiDailyBudgetUsd = 1
        };
        var bodies = new[]
        {
            "{\"messages\":[{\"role\":\"user\",\"content\":\"ABC-123\"}],\"max_tokens\":128}",
            "{\"messages\":[{\"role\":\"user\",\"content\":\"第二部作品\"}],\"max_tokens\":256}"
        };
        var cloud = AiCostPreview.Build(config, false, bodies);
        var local = AiCostPreview.Build(config, true, bodies);
        var cost = bodies.Sum(body => AiRequestQueue.EstimateReservedUsd(config, body)) * 3;
        var checks = new Dictionary<string, bool>
        {
            ["cloudShowsSixAttemptMaximum"] = cloud.Contains("本轮 2 条，最多 6 次请求"),
            ["cloudShowsConfiguredReserve"] = cloud.Contains($"${cost:F4}") && cloud.Contains("每日预算 $1.00"),
            ["cloudDisclaimsProviderBilling"] = cloud.Contains("实际账单以服务商为准"),
            ["localShowsFourAttemptMaximum"] = local.Contains("本轮 2 条，最多 4 次请求"),
            ["localOmitsDollarAmount"] = !local.Contains('$') && local.Contains("不会发送到云端")
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(new { passed = checks.Values.All(value => value), checks,
            cloudPreview = cloud, localPreview = local }, new JsonSerializerOptions { WriteIndented = true }));
        return checks.Values.All(value => value) ? 0 : 1;
    }
}
