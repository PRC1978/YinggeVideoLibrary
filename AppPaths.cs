using System.IO;

namespace YinggeVideoLibrary;

/// <summary>
/// Single resolution point for the shared user data directory.
///
/// Production behaviour is unchanged: <c>%USERPROFILE%\.YinggeVideoLibrary</c>.
/// Acceptance and diagnostic runs may redirect it explicitly so that they never
/// read or write the real library. The redirection exists because seven
/// offscreen UI diagnostics create a MainWindow, which used to bind the real
/// directory with no way to substitute it; that made isolated verification of
/// category graph, folder page, search and large-library performance impossible.
///
/// Resolution order:
///   1. <c>--data-dir &lt;path&gt;</c> or <c>--data-dir=&lt;path&gt;</c> on the command line
///   2. the <c>YINGGE_DATA_DIR</c> environment variable
///   3. the historical per-user default
///
/// An explicit value is always normalised to a full path. Invalid input falls
/// back to the default rather than crashing a running library session, and the
/// fallback reason is kept in <see cref="RedirectError"/> for diagnostics.
/// </summary>
internal static class AppPaths
{
    internal const string DataDirectoryArgument = "--data-dir";
    internal const string DataDirectoryVariable = "YINGGE_DATA_DIR";

    static readonly Lazy<string> lazyDataDirectory = new(Resolve, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Absolute path of the data directory in use for this process.</summary>
    internal static string DataDirectory => lazyDataDirectory.Value;

    /// <summary>True when the path came from an argument or the environment variable.</summary>
    internal static bool IsRedirected { get; private set; }

    /// <summary>Raw value the caller supplied, before normalisation. Null when not redirected.</summary>
    internal static string? RedirectSource { get; private set; }

    /// <summary>Non-null when a supplied path was rejected and the default was used instead.</summary>
    internal static string? RedirectError { get; private set; }

    internal static string DefaultDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".YinggeVideoLibrary");

    static string Resolve()
    {
        var requested = ReadArgument(Environment.GetCommandLineArgs(), DataDirectoryArgument);
        var origin = DataDirectoryArgument;
        if (string.IsNullOrWhiteSpace(requested))
        {
            requested = Environment.GetEnvironmentVariable(DataDirectoryVariable);
            origin = DataDirectoryVariable;
        }
        if (string.IsNullOrWhiteSpace(requested)) return DefaultDataDirectory;

        var trimmed = requested.Trim().Trim('"');
        if (trimmed.Length == 0) return DefaultDataDirectory;
        try
        {
            var full = Path.GetFullPath(trimmed);
            if (!Path.IsPathFullyQualified(full))
                throw new InvalidDataException("数据目录必须是绝对路径。");
            IsRedirected = true;
            RedirectSource = origin + "=" + trimmed;
            return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex)
        {
            RedirectError = $"{origin} 指定的数据目录无法使用（{ex.Message}），已退回默认目录。";
            return DefaultDataDirectory;
        }
    }

    static string? ReadArgument(string[] args, string name)
    {
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument.Equals(name, StringComparison.OrdinalIgnoreCase))
                return index + 1 < args.Length ? args[index + 1] : null;
            if (argument.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                return argument[(name.Length + 1)..];
        }
        return null;
    }
}
