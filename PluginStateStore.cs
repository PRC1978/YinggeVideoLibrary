using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace YinggeVideoLibrary;

internal sealed class PluginState
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("enabled")] public List<string> Enabled { get; set; } = [];
    // Permission signature the user actually agreed to. A changed signature
    // invalidates the consent and the plugin stops running until re-confirmed.
    [JsonPropertyName("consents")] public Dictionary<string, string> Consents { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    [JsonPropertyName("lastErrors")] public Dictionary<string, PluginLastError> LastErrors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    [JsonPropertyName("settings")] public Dictionary<string, Dictionary<string, string>> Settings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore] public string? LoadError { get; set; }
}

internal sealed class PluginLastError
{
    [JsonPropertyName("at")] public DateTime At { get; set; }
    [JsonPropertyName("message")] public string Message { get; set; } = "";
}

// Plugin enablement and consent live in their own file so a plugin mistake can
// never make the library config unreadable.
internal static class PluginStateStore
{
    internal const string FileName = "plugins.json";

    static string PathFor(string dataDir) => Path.Combine(dataDir, FileName);

    internal static PluginState Load(string dataDir)
    {
        var path = PathFor(dataDir);
        if (!File.Exists(path)) return new PluginState();
        try
        {
            var state = JsonSerializer.Deserialize<PluginState>(File.ReadAllText(path));
            if (state is null) return new PluginState { LoadError = "plugins.json 内容为空，已按未启用处理。" };
            state.Enabled ??= [];
            state.Consents ??= new(StringComparer.OrdinalIgnoreCase);
            state.LastErrors ??= new(StringComparer.OrdinalIgnoreCase);
            state.Settings ??= new(StringComparer.OrdinalIgnoreCase);
            return state;
        }
        catch (Exception ex)
        {
            // A damaged plugin state must not stop the app or lose the library.
            return new PluginState { LoadError = "plugins.json 无法读取（" + ex.Message + "），已按未启用处理。" };
        }
    }

    internal static bool Save(string dataDir, PluginState state, out string error)
    {
        error = "";
        var path = PathFor(dataDir);
        var temporary = path + ".tmp";
        try
        {
            Directory.CreateDirectory(dataDir);
            state.SchemaVersion = 1;
            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(temporary, json, new UTF8Encoding(false));
            if (File.Exists(path))
            {
                var backup = path + ".bak";
                try { File.Copy(path, backup, true); } catch { }
            }
            File.Move(temporary, path, true);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            return false;
        }
    }

    internal static void RecordError(string dataDir, string pluginId, string message)
    {
        var state = Load(dataDir);
        state.LastErrors[pluginId] = new PluginLastError { At = DateTime.Now, Message = PluginLog.Redact(message) };
        Save(dataDir, state, out _);
    }

    internal static void ClearError(string dataDir, string pluginId)
    {
        var state = Load(dataDir);
        if (state.LastErrors.Remove(pluginId)) Save(dataDir, state, out _);
    }

    // Deep copy so a failed save cannot leave the in-memory state ahead of disk.
    internal static PluginState Clone(PluginState state) => new()
    {
        SchemaVersion = state.SchemaVersion,
        Enabled = state.Enabled.ToList(),
        Consents = new Dictionary<string, string>(state.Consents, StringComparer.OrdinalIgnoreCase),
        LastErrors = state.LastErrors.ToDictionary(
            pair => pair.Key,
            pair => new PluginLastError { At = pair.Value.At, Message = pair.Value.Message },
            StringComparer.OrdinalIgnoreCase),
        Settings = state.Settings.ToDictionary(pair => pair.Key,
            pair => new Dictionary<string, string>(pair.Value ?? new(), StringComparer.Ordinal),
            StringComparer.OrdinalIgnoreCase),
        LoadError = state.LoadError
    };

    internal static bool IsEnabled(PluginState state, string pluginId) =>
        state.Enabled.Contains(pluginId, StringComparer.OrdinalIgnoreCase);

    internal static bool ConsentMatches(PluginState state, PluginDescriptor plugin) =>
        plugin.Manifest is not null &&
        state.Consents.TryGetValue(plugin.Id, out var signature) &&
        string.Equals(signature, plugin.PermissionSignature, StringComparison.Ordinal);

    internal static Dictionary<string, string> EffectiveSettings(PluginState? state, PluginDescriptor plugin)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (plugin.Manifest is null) return result;
        var saved = state is not null && state.Settings.TryGetValue(plugin.Id, out var values) ? values : null;
        foreach (var setting in plugin.Manifest.Settings)
            result[setting.Key] = saved is not null && saved.TryGetValue(setting.Key, out var value) &&
                setting.Choices.Contains(value, StringComparer.Ordinal) ? value : setting.Default;
        return result;
    }
}

// Plugin output is untrusted. Logs keep enough to debug a failure but never a
// full path, a key-looking token or a whole response body.
internal static class PluginLog
{
    const int MaxLogBytes = 64 * 1024;
    static readonly Regex WindowsPath = new(@"[A-Za-z]:\\[^\s""'<>|]*", RegexOptions.Compiled);
    static readonly Regex UncPath = new(@"\\\\[^\s""'<>|]+", RegexOptions.Compiled);
    static readonly Regex LongToken = new(@"[A-Za-z0-9_\-]{40,}", RegexOptions.Compiled);

    internal static string Redact(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var value = text.Length > 600 ? text[..600] + "…" : text;
        value = WindowsPath.Replace(value, "<path>");
        value = UncPath.Replace(value, "<path>");
        value = LongToken.Replace(value, "<token>");
        return value.Replace("\r", " ").Replace("\n", " ");
    }

    internal static void Append(string dataDir, string pluginId, string message)
    {
        if (string.IsNullOrWhiteSpace(dataDir)) return;
        try
        {
            var folder = Path.Combine(dataDir, "plugins", "logs");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, SafeName(pluginId) + ".log");
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {Redact(message)}{Environment.NewLine}";
            if (File.Exists(path) && new FileInfo(path).Length > MaxLogBytes)
                File.Move(path, path + ".1", true);
            File.AppendAllText(path, line, new UTF8Encoding(false));
        }
        catch { /* Logging must never break a scrape. */ }
    }

    static string SafeName(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
            builder.Append(char.IsLetterOrDigit(character) || character is '.' or '-' or '_' ? character : '_');
        return builder.Length == 0 ? "plugin" : builder.ToString();
    }
}
