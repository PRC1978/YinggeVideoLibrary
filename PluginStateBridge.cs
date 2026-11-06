namespace YinggeVideoLibrary;

internal sealed class PluginStateBridge(string dataDir, PostgresLibraryRuntimeSession? session)
{
    private PostgresUserStateStore.Snapshot<PluginState>? baseline;

    internal PluginState Load()
    {
        if (session is null) return PluginStateStore.Load(dataDir);
        baseline = Task.Run(() => PostgresUserStateStore.LoadPluginStateAsync(
            session.ConnectionString)).GetAwaiter().GetResult();
        return PluginStateStore.Clone(baseline.Value);
    }

    internal bool Save(PluginState edited, out string error)
    {
        if (session is null) return PluginStateStore.Save(dataDir, edited, out error);
        try
        {
            var previous = baseline ?? throw new InvalidOperationException("插件状态尚未载入。");
            baseline = Task.Run(() => PostgresUserStateStore.SavePluginStateAsync(
                session.ConnectionString, previous, edited)).GetAwaiter().GetResult();
            error = "";
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    internal PluginState ChangeError(string pluginId, string? message)
    {
        if (session is null)
        {
            if (message is null) PluginStateStore.ClearError(dataDir, pluginId);
            else PluginStateStore.RecordError(dataDir, pluginId, message);
            return Load();
        }
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var current = Load();
            if (message is null)
            {
                if (!current.LastErrors.Remove(pluginId)) return current;
            }
            else current.LastErrors[pluginId] = new PluginLastError
            {
                At = DateTime.Now, Message = PluginLog.Redact(message)
            };
            if (Save(current, out var error)) return current;
            if (attempt == 2) throw new InvalidOperationException(
                "插件错误状态未能保存：" + error);
        }
        throw new InvalidOperationException("插件状态未能保存。");
    }
}
