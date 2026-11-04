using System.IO;
using System.Text.Json;

namespace YinggeVideoLibrary;

internal sealed class ErrorUploadSettings
{
    const string CredentialName = "ErrorUploadSettings";
    public string Target { get; set; } = "";
    public string Port { get; set; } = "22";
    public string KeyPath { get; set; } = "";

    static string DirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YinggeVideoLibrary");
    internal static string FilePath => Path.Combine(DirectoryPath, "error-upload-settings.json");

    internal static ErrorUploadSettings Load()
    {
        try
        {
            var protectedValue = WindowsCredentialStore.Read(CredentialName);
            if (!string.IsNullOrWhiteSpace(protectedValue))
            {
                var saved = JsonSerializer.Deserialize<ErrorUploadSettings>(protectedValue);
                if (saved is not null && saved.IsValid())
                {
                    // A prior build saved the endpoint in a plain local JSON.
                    // Remove that exact file only after the protected copy exists.
                    if (File.Exists(FilePath)) File.Delete(FilePath);
                    return saved;
                }
            }
            if (!File.Exists(FilePath)) return new ErrorUploadSettings();
            var legacy = JsonSerializer.Deserialize<ErrorUploadSettings>(File.ReadAllText(FilePath));
            if (legacy is null || !legacy.IsValid()) return new ErrorUploadSettings();
            legacy.Save();
            return legacy;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                   or System.ComponentModel.Win32Exception)
        { return new ErrorUploadSettings(); }
    }

    bool IsValid() => SshEncryptedReportUploader.ValidTarget(Target) &&
        SshEncryptedReportUploader.ValidPort(Port) &&
        Path.IsPathFullyQualified(KeyPath) && File.Exists(KeyPath);

    internal void Save()
    {
        if (!IsValid())
            throw new InvalidDataException("服务器、端口或 SSH 私钥路径无效。 ");
        WindowsCredentialStore.Write(CredentialName, JsonSerializer.Serialize(this));
        if (File.Exists(FilePath)) File.Delete(FilePath);
    }
}
