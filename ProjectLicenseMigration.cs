using System.IO;
using System.Reflection;

namespace YinggeVideoLibrary;

internal static class ProjectLicenseMigration
{
    // The portable updater replaces the executable. An older installer also
    // leaves a license sidecar beside it, so refresh only our exact old notice.
    internal static void RefreshInstalledSidecar()
    {
        try
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable)) return;
            var directory = Path.GetDirectoryName(executable);
            if (string.IsNullOrWhiteSpace(directory)) return;
            RefreshSidecar(Path.Combine(directory, "LICENSE"));
        }
        catch (IOException) { /* Read-only portable folders still expose the embedded license. */ }
        catch (UnauthorizedAccessException) { /* Installation may be read-only. */ }
    }

    internal static bool RefreshSidecar(string path)
    {
        if (!File.Exists(path)) return false;
        var old = File.ReadAllText(path);
        if (!old.StartsWith("MIT License", StringComparison.Ordinal) ||
            !old.Contains("Copyright (c) 2026 PRC1978", StringComparison.Ordinal))
            return false;

        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("YinggeVideoLibrary.LICENSE")
            ?? throw new InvalidDataException("程序缺少项目许可文本。 ");
        using var reader = new StreamReader(stream);
        var current = reader.ReadToEnd();
        if (!current.Contains("PolyForm Noncommercial License 1.0.0", StringComparison.Ordinal))
            throw new InvalidDataException("程序内项目许可文本不匹配。 ");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, current);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        return true;
    }

    internal static bool VerifyForDiagnostics(string directory)
    {
        Directory.CreateDirectory(directory);
        var legacy = Path.Combine(directory, "LICENSE");
        File.WriteAllText(legacy, "MIT License\n\nCopyright (c) 2026 PRC1978\n");
        var migrated = RefreshSidecar(legacy) &&
            File.ReadAllText(legacy).Contains("PolyForm Noncommercial License 1.0.0",
                StringComparison.Ordinal) && !RefreshSidecar(legacy);
        var unrelated = Path.Combine(directory, "unrelated-LICENSE");
        File.WriteAllText(unrelated, "MIT License\n\nCopyright another author\n");
        return migrated && !RefreshSidecar(unrelated) &&
            File.ReadAllText(unrelated).Contains("Copyright another author", StringComparison.Ordinal);
    }
}
