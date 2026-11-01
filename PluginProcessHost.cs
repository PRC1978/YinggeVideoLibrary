using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace YinggeVideoLibrary;

// Runs one request in a dedicated child process. A crash, a hang or a huge
// response costs one process, never the main window: the call is asynchronous,
// every wait is bounded, and the process tree is killed when the budget expires.
internal static class PluginProcessHost
{
    internal const int MaxLineChars = 256 * 1024;
    internal const int MaxStderrChars = 4 * 1024;
    const int MaxSkippedLines = 8;
    static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    internal static async Task<PluginHello?> DescribeAsync(PluginDescriptor plugin, CancellationToken cancellationToken)
    {
        var request = new PluginRequest { Id = Guid.NewGuid().ToString("N"), Method = PluginProtocol.MethodDescribe };
        var response = await InvokeAsync(plugin, request, plugin.Timeout, cancellationToken);
        if (!response.Ok || response.Payload is not { } payload) return null;
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            var name = root.TryGetProperty("name", out var nameNode) ? nameNode.GetString() ?? "" : "";
            var version = root.TryGetProperty("version", out var versionNode) ? versionNode.GetString() ?? "" : "";
            var capabilities = root.TryGetProperty("capabilities", out var capabilityNode) && capabilityNode.ValueKind == JsonValueKind.Array
                ? capabilityNode.EnumerateArray().Select(item => item.GetString() ?? "").Where(item => item.Length > 0).ToList()
                : [];
            return new PluginHello(name, version, capabilities);
        }
        catch (JsonException) { return null; }
    }

    internal static async Task<PluginSearchOutcome> SearchAsync(PluginDescriptor plugin,
        PluginSearchParameters parameters, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        var request = new PluginRequest
        {
            Id = Guid.NewGuid().ToString("N"),
            Method = PluginProtocol.MethodSearch,
            Parameters = parameters
        };
        var response = await InvokeAsync(plugin, request, plugin.Timeout, cancellationToken);
        if (!response.Ok)
            return PluginSearchOutcome.Failure(response.ErrorCode, response.ErrorMessage, clock.ElapsedMilliseconds, response.RetryAfter);
        if (response.Payload is not { } payload)
            return PluginSearchOutcome.Failure("empty_response", "插件没有返回结果。", clock.ElapsedMilliseconds);
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (!root.TryGetProperty("candidates", out var array) || array.ValueKind != JsonValueKind.Array)
                return PluginSearchOutcome.Failure("invalid_result", "插件结果缺少 candidates 数组。", clock.ElapsedMilliseconds);
            var candidates = new List<PluginCandidate>();
            var dropped = 0;
            foreach (var item in array.EnumerateArray().Take(PluginProtocol.MaxCandidates))
            {
                if (TryReadCandidate(item, plugin, out var candidate, out _)) candidates.Add(candidate!);
                else dropped++;
            }
            return new PluginSearchOutcome(true, "", "", null, candidates, clock.ElapsedMilliseconds, dropped);
        }
        catch (JsonException ex)
        {
            return PluginSearchOutcome.Failure("invalid_json", "插件结果不是有效 JSON：" + ex.Message, clock.ElapsedMilliseconds);
        }
    }

    static bool TryReadCandidate(JsonElement item, PluginDescriptor plugin, out PluginCandidate? candidate, out string reason)
    {
        candidate = null;
        reason = "";
        if (item.ValueKind != JsonValueKind.Object) { reason = "候选不是对象"; return false; }
        var title = ReadString(item, "title");
        if (string.IsNullOrWhiteSpace(title) || title.Length > PluginProtocol.MaxTitleLength) { reason = "标题为空或过长"; return false; }
        var sourceUrl = ReadString(item, "sourceUrl");
        if (!IsAllowedUrl(sourceUrl, plugin)) { reason = "来源链接缺失、不是 http(s) 或不在声明的域名内"; return false; }
        var posterUrl = ReadString(item, "posterUrl");
        if (posterUrl.Length > 0 && !IsAllowedUrl(posterUrl, plugin)) { reason = "封面链接不在声明的域名内"; posterUrl = ""; }
        // A plugin must never leak a local path back into the library. Any field
        // that looks like a path is rejected instead of being stored.
        if (LooksLikePath(title) || LooksLikePath(ReadString(item, "code"))) { reason = "候选包含本地路径"; return false; }
        var code = ReadString(item, "code");
        var year = item.TryGetProperty("year", out var yearNode) && yearNode.TryGetInt32(out var parsedYear) && parsedYear is > 1800 and < 2200
            ? parsedYear : (int?)null;
        var hint = item.TryGetProperty("confidenceHint", out var hintNode) && hintNode.TryGetInt32(out var parsedHint)
            ? Math.Clamp(parsedHint, 0, 100) : 50;
        candidate = new PluginCandidate(
            title.Trim(), ReadString(item, "originalTitle") is { Length: > 0 } original ? original : null,
            code.Length > 0 ? code : null, year,
            string.IsNullOrWhiteSpace(ReadString(item, "source")) ? plugin.DisplayName : ReadString(item, "source"),
            sourceUrl, posterUrl.Length > 0 ? posterUrl : null,
            ReadString(item, "summary") is { Length: > 0 } summary ? summary : null,
            ReadString(item, "evidence"), hint);
        return true;
    }

    static string ReadString(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var node) || node.ValueKind != JsonValueKind.String) return "";
        var value = node.GetString() ?? "";
        return value.Length > PluginProtocol.MaxUrlLength ? value[..PluginProtocol.MaxUrlLength] : value;
    }

    static bool IsAllowedUrl(string url, PluginDescriptor plugin)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
        var declared = plugin.Manifest?.Permissions?.Network ?? [];
        // A plugin with no declared domain may only point at the host it was
        // installed for; an undeclared link is refused rather than shown.
        if (declared.Count == 0) return false;
        return declared.Any(host => uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase));
    }

    // A local path in a candidate field is never acceptable, whatever the
    // separator style or drive-letter case.
    static bool LooksLikePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (value.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return true;
        if (value.StartsWith("/", StringComparison.Ordinal) || value.StartsWith("\\\\", StringComparison.Ordinal)) return true;
        return System.Text.RegularExpressions.Regex.IsMatch(value, @"^[A-Za-z]:[\\/]");
    }

    sealed record InvocationResult(bool Ok, string ErrorCode, string ErrorMessage, TimeSpan? RetryAfter, string? Payload);

    static async Task<InvocationResult> InvokeAsync(PluginDescriptor plugin, PluginRequest request,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (plugin.Manifest is null)
            return new InvocationResult(false, "invalid_plugin", "插件清单不可用。", null, null);
        var entry = plugin.EntryPath;
        if (!File.Exists(entry))
            return new InvocationResult(false, "missing_entry", "找不到插件入口文件。", null, null);
        if (timeout <= TimeSpan.Zero) timeout = DefaultTimeout;
        if (timeout > TimeSpan.FromSeconds(120)) timeout = TimeSpan.FromSeconds(120);

        var startInfo = new ProcessStartInfo(entry)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = plugin.FolderPath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false)
        };
        // The child inherits the environment, so drop anything that looks like a
        // secret or a data-directory hint before it starts.
        foreach (var name in startInfo.Environment.Keys.ToList())
        {
            if (name.Equals(AppPaths.DataDirectoryVariable, StringComparison.OrdinalIgnoreCase) ||
                name.Contains("API_KEY", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("TOKEN", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("SECRET", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase))
                startInfo.Environment.Remove(name);
        }
        startInfo.Environment["YINGGE_PLUGIN_API"] = "1";

        using var process = new Process { StartInfo = startInfo };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            if (!process.Start())
                return new InvocationResult(false, "start_failed", "插件进程无法启动。", null, null);
        }
        catch (Exception ex)
        {
            return new InvocationResult(false, "start_failed", "插件进程无法启动：" + ex.Message, null, null);
        }
        // StandardError is only readable after Start(); reading it concurrently
        // keeps a chatty plugin from filling the pipe and blocking.
        var stderrTask = Task.Run(() => ReadBoundedAsync(process.StandardError, MaxStderrChars, deadline.Token));

        try
        {
            await process.StandardInput.WriteLineAsync(PluginProtocol.Encode(request)).WaitAsync(deadline.Token);
            await process.StandardInput.FlushAsync(deadline.Token);
            // Keep stdin open: a plugin that reads to EOF would otherwise stop
            // before answering. Closing is done after the response arrives.

            for (var attempt = 0; attempt < MaxSkippedLines; attempt++)
            {
                var line = await process.StandardOutput.ReadLineAsync(deadline.Token);
                if (line is null)
                {
                    var exitCode = SafeExitCode(process);
                    var stderr = await SafeStderrAsync(stderrTask);
                    var detail = string.IsNullOrWhiteSpace(stderr) ? "" : "：" + stderr;
                    return new InvocationResult(false, "plugin_exited",
                        $"插件未返回结果就退出（退出码 {exitCode}）{detail}", null, null);
                }
                if (line.Length > MaxLineChars)
                    return new InvocationResult(false, "response_too_large",
                        $"插件响应超过 {MaxLineChars / 1024} KB，已丢弃。", null, null);
                if (string.IsNullOrWhiteSpace(line)) continue;
                PluginResponse? response;
                try { response = JsonSerializer.Deserialize<PluginResponse>(line); }
                catch (JsonException) { continue; }
                if (response is null || !string.Equals(response.Id, request.Id, StringComparison.Ordinal)) continue;
                if (!response.Ok)
                {
                    var error = response.Error ?? new PluginErrorPayload();
                    var retryAfter = error.RetryAfterSeconds is > 0 and <= 86400
                        ? TimeSpan.FromSeconds(error.RetryAfterSeconds!.Value) : (TimeSpan?)null;
                    return new InvocationResult(false, string.IsNullOrWhiteSpace(error.Code) ? "plugin_error" : error.Code,
                        string.IsNullOrWhiteSpace(error.Message) ? "插件报告了一个错误。" : error.Message, retryAfter, null);
                }
                return new InvocationResult(true, "", "", null, response.Result.GetRawText());
            }
            return new InvocationResult(false, "invalid_response", "插件没有返回可识别的响应。", null, null);
        }
        catch (OperationCanceledException)
        {
            var reason = cancellationToken.IsCancellationRequested ? "cancelled" : "timeout";
            return new InvocationResult(false, reason,
                reason == "timeout" ? $"插件在 {timeout.TotalSeconds:0} 秒内没有响应，已停止。" : "已取消插件调用。", null, null);
        }
        catch (Exception ex)
        {
            return new InvocationResult(false, "invoke_failed", "插件调用失败：" + ex.Message, null, null);
        }
        finally
        {
            try { process.StandardInput.Close(); } catch { }
            try { if (!process.HasExited) process.Kill(true); } catch { }
            try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        }
    }

    static int SafeExitCode(Process process)
    {
        try { return process.HasExited ? process.ExitCode : -1; } catch { return -1; }
    }

    static async Task<string> SafeStderrAsync(Task<string> task)
    {
        try { return await task.WaitAsync(TimeSpan.FromSeconds(2)); } catch { return ""; }
    }

    static async Task<string> ReadBoundedAsync(StreamReader reader, int limit, CancellationToken cancellationToken)
    {
        var buffer = new char[1024];
        var builder = new StringBuilder();
        try
        {
            while (builder.Length < limit)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0) break;
                builder.Append(buffer, 0, Math.Min(read, limit - builder.Length));
            }
        }
        catch { }
        return PluginLog.Redact(builder.ToString());
    }
}
