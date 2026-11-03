namespace YinggeVideoLibrary;

internal static class AiCostPreview
{
    internal static string Build(LibraryConfig config, bool localEndpoint, IReadOnlyList<string> requestBodies)
    {
        var retries = Math.Clamp(localEndpoint ? config.AiLocalRetryCount : config.AiCloudRetryCount, 0, 5);
        var attempts = checked(requestBodies.Count * (retries + 1));
        var heading = $"本轮 {requestBodies.Count} 条，最多 {attempts} 次请求（含重试）。\n";
        if (localEndpoint)
            return heading + (config.AiEndpoint.StartsWith("plugin-ai://", StringComparison.OrdinalIgnoreCase)
                ? "当前使用本机 AI 插件；插件会收到提示文本，进程本身不受网络隔离。\n开始评估？"
                : "当前使用本机模型；不会发送到云端。\n开始评估？");

        var perAttempt = requestBodies.Sum(body => AiRequestQueue.EstimateReservedUsd(config, body));
        var maximum = perAttempt * (retries + 1);
        return heading +
            $"按设置费率估算的预留费用上限约 ${maximum:F4}；每日预算 ${config.AiDailyBudgetUsd:F2}。实际账单以服务商为准。\n开始评估？";
    }
}
