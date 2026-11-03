using System.IO;

namespace YinggeVideoLibrary;

// A local model plugin uses the same durable AI job and budget pipeline as an
// HTTP model. The URL identifies an enabled plugin, never a network target.
internal static class PluginAiTransport
{
    internal const string Scheme = "plugin-ai";

    internal static bool IsEndpoint(Uri uri) => uri.Scheme.Equals(Scheme, StringComparison.OrdinalIgnoreCase);

    internal static PluginDescriptor Resolve(Uri uri, string dataDir)
    {
        if (!IsEndpoint(uri) || uri.UserInfo.Length > 0 || uri.Query.Length > 0 ||
            uri.Fragment.Length > 0 || uri.AbsolutePath != "/" || uri.Port != -1)
            throw new InvalidOperationException("AI 插件地址必须是 plugin-ai://插件ID，不能包含路径或参数。");
        var matches = PluginManifestReader.Scan(Path.Combine(dataDir, "plugins"))
            .Where(item => item.Id.Equals(uri.Host, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count != 1 || matches[0].Status != PluginStatus.Ready ||
            matches[0].Manifest?.Capabilities.Contains("ai.service", StringComparer.OrdinalIgnoreCase) != true ||
            matches[0].Manifest!.Permissions.Network.Count > 0)
            throw new InvalidOperationException("AI 插件不存在、版本不兼容或未声明本机模型能力。");
        var state = PluginStateStore.Load(dataDir);
        if (!PluginStateStore.IsEnabled(state, matches[0].Id) ||
            !PluginStateStore.ConsentMatches(state, matches[0]))
            throw new InvalidOperationException("AI 插件未启用或权限尚未确认。");
        return matches[0];
    }
}
