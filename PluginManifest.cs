using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace YinggeVideoLibrary;

// The first plugin batch only opens scrape sources. Every other capability
// name is recognised so a future manifest can be reported as "not supported
// yet" instead of "damaged", but nothing outside this set is loadable.
internal enum PluginStatus
{
    Ready,
    MissingManifest,
    DamagedManifest,
    DuplicateId,
    IncompatibleApi,
    UnsupportedCapability,
    UnsafeEntry,
    MissingEntry,
    Disabled
}

internal sealed class PluginPermissions
{
    // Host names only. A plugin never receives an API key, a cookie or a URL
    // with credentials; the list is shown to the user before it is enabled.
    [JsonPropertyName("network")] public List<string> Network { get; set; } = [];
    // Data scopes the host may send. Unknown scopes make the plugin unusable
    // rather than silently sending more than the user agreed to.
    [JsonPropertyName("data")] public List<string> Data { get; set; } = [];
}

internal sealed class PluginEntry
{
    [JsonPropertyName("file")] public string File { get; set; } = "";
    [JsonPropertyName("arguments")] public List<string> Arguments { get; set; } = [];
}

internal sealed class PluginManifest
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("apiVersion")] public int ApiVersion { get; set; } = 1;
    [JsonPropertyName("author")] public string Author { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("homepage")] public string Homepage { get; set; } = "";
    [JsonPropertyName("capabilities")] public List<string> Capabilities { get; set; } = [];
    [JsonPropertyName("permissions")] public PluginPermissions Permissions { get; set; } = new();
    [JsonPropertyName("entry")] public PluginEntry Entry { get; set; } = new();
    [JsonPropertyName("timeoutSeconds")] public int TimeoutSeconds { get; set; } = 20;
}

// One discovered folder, whether or not it is usable. The management page
// lists rejected folders too: a plugin the user installed must never vanish
// silently because its manifest stopped parsing.
internal sealed class PluginDescriptor
{
    public string FolderPath { get; init; } = "";
    public string FolderName { get; init; } = "";
    public PluginManifest? Manifest { get; init; }
    public PluginStatus Status { get; set; }
    public string Message { get; set; } = "";
    public bool Enabled { get; set; }
    public string PermissionSignature { get; set; } = "";
    public string? LastError { get; set; }
    public DateTime? LastErrorAt { get; set; }

    public string Id => Manifest?.Id ?? "";
    public string DisplayName => string.IsNullOrWhiteSpace(Manifest?.Name) ? FolderName : Manifest!.Name;
    public bool Loadable => Status is PluginStatus.Ready or PluginStatus.Disabled
        && Manifest is not null && Status != PluginStatus.Disabled;
    public string EntryPath => Manifest is null ? "" : Path.Combine(FolderPath, Manifest.Entry.File);
    public TimeSpan Timeout => TimeSpan.FromSeconds(Math.Clamp(Manifest?.TimeoutSeconds ?? 20, 1, 120));
}

internal static class PluginManifestReader
{
    internal const int SupportedApiVersion = 1;
    internal const int MaxManifestBytes = 64 * 1024;
    internal const int MaxPlugins = 64;

    // Declared, recognised, but not implemented in this batch. Kept apart from
    // "unknown capability" so the page can say "下一版支持" instead of "无效".
    static readonly HashSet<string> KnownCapabilities =
        new(StringComparer.OrdinalIgnoreCase) { "scrape.source", "ai.service", "tool.command", "ui.theme" };
    static readonly HashSet<string> SupportedCapabilities =
        new(StringComparer.OrdinalIgnoreCase) { "scrape.source" };
    // Anything else would have to be sent to the plugin, so it is refused
    // until a later batch implements and documents it.
    static readonly HashSet<string> SupportedDataScopes =
        new(StringComparer.OrdinalIgnoreCase) { "query.text", "query.code", "query.year" };

    static readonly Regex IdPattern = new("^[a-z0-9][a-z0-9._-]{2,63}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex VersionPattern = new(@"^\d+\.\d+\.\d+([-+][0-9A-Za-z.-]+)?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex HostPattern = new(@"^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    internal static List<PluginDescriptor> Scan(string pluginRoot)
    {
        var descriptors = new List<PluginDescriptor>();
        if (!Directory.Exists(pluginRoot)) return descriptors;
        IEnumerable<string> folders;
        try { folders = Directory.EnumerateDirectories(pluginRoot).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).Take(MaxPlugins); }
        catch (Exception ex)
        {
            descriptors.Add(new PluginDescriptor
            {
                FolderPath = pluginRoot, FolderName = Path.GetFileName(pluginRoot),
                Status = PluginStatus.DamagedManifest, Message = "插件目录无法读取：" + ex.Message
            });
            return descriptors;
        }
        foreach (var folder in folders) descriptors.Add(Read(folder));

        // Duplicate ids are a security problem, not a convenience problem: two
        // folders claiming one id would make permission consent ambiguous.
        foreach (var group in descriptors.Where(item => !string.IsNullOrWhiteSpace(item.Id))
                     .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
        {
            foreach (var duplicate in group)
            {
                duplicate.Status = PluginStatus.DuplicateId;
                duplicate.Message = $"插件 ID 与另一个文件夹重复：{group.Key}。请只保留一个。";
            }
        }
        return descriptors;
    }

    internal static PluginDescriptor Read(string folder)
    {
        var descriptor = new PluginDescriptor { FolderPath = folder, FolderName = Path.GetFileName(folder) };
        var manifestPath = Path.Combine(folder, "plugin.json");
        try
        {
            if (!File.Exists(manifestPath))
            {
                descriptor.Status = PluginStatus.MissingManifest;
                descriptor.Message = "缺少 plugin.json。";
                return descriptor;
            }
            if (new FileInfo(manifestPath).Length > MaxManifestBytes)
            {
                descriptor.Status = PluginStatus.DamagedManifest;
                descriptor.Message = "plugin.json 超过 64 KB。";
                return descriptor;
            }
            PluginManifest? manifest;
            try { manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(manifestPath)); }
            catch (JsonException ex)
            {
                descriptor.Status = PluginStatus.DamagedManifest;
                descriptor.Message = "plugin.json 不是有效 JSON：" + ex.Message;
                return descriptor;
            }
            if (manifest is null)
            {
                descriptor.Status = PluginStatus.DamagedManifest;
                descriptor.Message = "plugin.json 内容为空。";
                return descriptor;
            }
            descriptor = new PluginDescriptor
            {
                FolderPath = folder, FolderName = Path.GetFileName(folder), Manifest = manifest,
                PermissionSignature = Signature(manifest.Permissions)
            };
            descriptor.Status = Validate(descriptor, folder, manifest, out var message);
            descriptor.Message = message;
            return descriptor;
        }
        catch (Exception ex)
        {
            descriptor.Status = PluginStatus.DamagedManifest;
            descriptor.Message = "读取插件清单失败：" + ex.Message;
            return descriptor;
        }
    }

    static PluginStatus Validate(PluginDescriptor descriptor, string folder, PluginManifest manifest, out string message)
    {
        message = "";
        if (!IdPattern.IsMatch(manifest.Id ?? ""))
        {
            message = "插件 ID 只能使用小写字母、数字、点、下划线和连字符，长度 3–64。";
            return PluginStatus.DamagedManifest;
        }
        if (!VersionPattern.IsMatch(manifest.Version ?? ""))
        {
            message = "版本号必须是 1.0.0 形式。";
            return PluginStatus.DamagedManifest;
        }
        if (string.IsNullOrWhiteSpace(manifest.Name))
        {
            message = "缺少插件名称。";
            return PluginStatus.DamagedManifest;
        }
        if (manifest.ApiVersion != SupportedApiVersion)
        {
            message = $"插件接口版本 {manifest.ApiVersion} 与本程序支持的版本 {SupportedApiVersion} 不兼容。";
            return PluginStatus.IncompatibleApi;
        }
        if (manifest.Capabilities is null || manifest.Capabilities.Count == 0)
        {
            message = "插件没有声明任何能力。";
            return PluginStatus.DamagedManifest;
        }
        var unknown = manifest.Capabilities.Where(capability => !KnownCapabilities.Contains(capability)).ToList();
        if (unknown.Count > 0)
        {
            message = "无法识别的能力：" + string.Join("、", unknown);
            return PluginStatus.UnsupportedCapability;
        }
        var unsupported = manifest.Capabilities.Where(capability => !SupportedCapabilities.Contains(capability)).ToList();
        if (unsupported.Count > 0)
        {
            message = "本版本尚未开放的能力：" + string.Join("、", unsupported);
            return PluginStatus.UnsupportedCapability;
        }
        var dataScopes = manifest.Permissions?.Data ?? [];
        var badScope = dataScopes.FirstOrDefault(scope => !SupportedDataScopes.Contains(scope));
        if (badScope is not null)
        {
            message = $"未支持的数据权限：{badScope}。本版本只发送 {string.Join("、", SupportedDataScopes)}。";
            return PluginStatus.UnsupportedCapability;
        }
        var hosts = manifest.Permissions?.Network ?? [];
        if (hosts.Count > 16)
        {
            message = "网络域名最多声明 16 个。";
            return PluginStatus.DamagedManifest;
        }
        var badHost = hosts.FirstOrDefault(host => !HostPattern.IsMatch(host ?? ""));
        if (badHost is not null)
        {
            message = $"网络权限必须是不含路径的域名，实际为：{badHost}";
            return PluginStatus.DamagedManifest;
        }
        if (!IsSafeRelative(manifest.Entry?.File))
        {
            message = "入口文件必须是插件目录内的相对路径。";
            return PluginStatus.UnsafeEntry;
        }
        var entryPath = Path.GetFullPath(Path.Combine(folder, manifest.Entry!.File));
        if (!IsInside(folder, entryPath))
        {
            message = "入口文件不在插件目录内。";
            return PluginStatus.UnsafeEntry;
        }
        if (!File.Exists(entryPath))
        {
            message = "找不到入口文件：" + manifest.Entry.File;
            return PluginStatus.MissingEntry;
        }
        return PluginStatus.Ready;
    }

    internal static bool IsSafeRelative(string? path) =>
        !string.IsNullOrWhiteSpace(path) && !Path.IsPathRooted(path) && !path.Contains(':') &&
        !path.Split('/', '\\').Any(segment => segment is ".." or "." or "") &&
        path.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    static bool IsInside(string root, string candidate)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    // Stable signature of the permission set. A plugin that silently adds a
    // domain or a data scope must be re-confirmed by the user.
    internal static string Signature(PluginPermissions? permissions)
    {
        if (permissions is null) return "";
        var text = string.Join("|", (permissions.Network ?? []).Select(item => "n:" + item.Trim().ToLowerInvariant())
            .Concat((permissions.Data ?? []).Select(item => "d:" + item.Trim().ToLowerInvariant()))
            .OrderBy(item => item, StringComparer.Ordinal));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash).ToLowerInvariant()[..32];
    }
}
