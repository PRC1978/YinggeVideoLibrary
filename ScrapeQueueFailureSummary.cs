namespace YinggeVideoLibrary;

// Error strings can contain local paths, query titles, URLs or secrets supplied
// by third-party sources. Never render a raw LastError or use it as a group key.
internal static class ScrapeQueueFailureSummary
{
    internal static string Category(string? error)
    {
        var text = error ?? "";
        bool Has(params string[] words) => words.Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));
        if (Has("缺少可搜索标题", "缺少可搜索的标题", "缺少有效标题", "请先手动补充标题")) return "缺少可搜索标题";
        if (Has("429", "限速", "限流", "冷却", "过于频繁", "rate limit")) return "来源限速或冷却";
        if (Has("403", "401", "验证码", "人机", "访问验证", "年龄验证", "验证页面", "验证页", "forbidden", "unauthorized")) return "来源访问验证或权限限制";
        if (Has("timeout", "timed out", "超时", "连接", "网络", "SSL", "TLS", "DNS", "socket")) return "网络连接或超时";
        if (Has("解析", "JSON", "格式", "结构", "搜索条目缺少", "详情条目缺少")) return "来源格式或资料结构变化";
        if (Has("没有候选", "未找到", "无匹配", "没有找到", "没有可用结果", "身份", "不一致")) return "候选缺失或身份核对未通过";
        if (Has("保存", "写入", "只读", "磁盘", "文件访问", "IOException")) return "本机读写或保存失败";
        if (Has("未启用", "没有可用的搜刮来源", "没有可用来源", "没有启用")) return "来源配置未就绪";
        return "其他失败（需逐项重新核对）";
    }

    internal static string Build(IEnumerable<string?> errors)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var total = 0;
        foreach (var error in errors)
        {
            var category = Category(error);
            counts[category] = counts.GetValueOrDefault(category) + 1;
            total++;
        }
        var lines = new List<string> { $"失败任务：{total:N0} 项" };
        if (total == 0) lines.Add("当前没有标记为失败的任务。");
        else
            lines.AddRange(counts.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"• {pair.Key}：{pair.Value:N0} 项"));
        lines.Add("");
        lines.Add("已失败的旧任务不会因更新软件而自动重试。修正条件后，请手动点击“重试失败”；如轮询暂停，再点击“继续轮询”。");
        lines.Add("缺少标题时，请先通过单条重新搜刮补充正确标题或番号，再重新入队。只有数字、分辨率等内容不足以可靠搜索。");
        lines.Add("以上是按错误信息关键词归类的数量，仅供排查；不会显示私有标题、路径或错误原文，也不会修改队列或视频资料。");
        return string.Join(Environment.NewLine, lines);
    }

    internal static IReadOnlyDictionary<string, bool> VerifyForDiagnostics()
    {
        const string secret = "PRIVATE_QUERY_784321";
        var summary = Build(new[]
        {
            "文件名和人物目录下的作品目录均缺少可搜索标题；请先手动补充标题后再搜刮。",
            "来源429 rate limited " + secret,
            "来源429 rate limited C:\\Private\\secret.mp4",
            "SSL connection failed https://private.invalid/" + secret,
            "unknown token=SECRET_123456"
        });
        return new Dictionary<string, bool>
        {
            ["failureSummaryCountsDuplicateCategories"] = summary.Contains("来源限速或冷却：2 项", StringComparison.Ordinal),
            ["failureSummaryRecognizesMissingTitle"] = summary.Contains("缺少可搜索标题：1 项", StringComparison.Ordinal),
            ["failureSummaryNeverDisplaysRawErrors"] = !summary.Contains(secret, StringComparison.Ordinal)
                && !summary.Contains("secret.mp4", StringComparison.Ordinal) && !summary.Contains("private.invalid", StringComparison.Ordinal)
                && !summary.Contains("SECRET_123456", StringComparison.Ordinal),
            ["failureSummaryExplainsManualRetry"] = summary.Contains("手动点击“重试失败”", StringComparison.Ordinal),
            ["failureSummaryEmptyQueueIsExplicit"] = Build(Array.Empty<string>()).Contains("当前没有标记为失败", StringComparison.Ordinal)
        };
    }
}
