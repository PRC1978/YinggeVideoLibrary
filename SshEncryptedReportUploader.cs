using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace YinggeVideoLibrary;

internal static partial class SshEncryptedReportUploader
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);

    internal static bool ValidTarget(string value) => TargetPattern().IsMatch(value);
    internal static bool ValidPort(string value) => int.TryParse(value, out var port) && port is >= 1 and <= 65535;

    internal static async Task<string> UploadAsync(byte[] encryptedBytes, string target, string port, string keyPath,
        CancellationToken cancellationToken = default)
    {
        if (!ValidTarget(target)) throw new ArgumentException("请填写 user@host 格式的服务器地址。", nameof(target));
        if (!ValidPort(port)) throw new ArgumentException("SSH 端口无效。", nameof(port));
        if (!File.Exists(keyPath)) throw new FileNotFoundException("SSH 私钥不存在。", keyPath);
        if (encryptedBytes.Length > 8 * 1024 * 1024 + 128 ||
            !EncryptedErrorReport.IsEncryptedBundle(encryptedBytes))
            throw new InvalidDataException("请选择有效的加密日志包。 ");
        var openSsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH");
        var ssh = Path.Combine(openSsh, "ssh.exe");
        var scp = Path.Combine(openSsh, "scp.exe");
        if (!File.Exists(ssh) || !File.Exists(scp))
            throw new FileNotFoundException("Windows OpenSSH 客户端未安装。 ");
        var filename = "yingge-error-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" +
            Guid.NewGuid().ToString("N")[..8] + EncryptedErrorReport.Extension;
        var temporary = Path.Combine(Path.GetTempPath(), "YinggeEncryptedUpload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        var staged = Path.Combine(temporary, filename);
        try
        {
            await File.WriteAllBytesAsync(staged, encryptedBytes, cancellationToken);
            await RunAsync(ssh, ["-i", keyPath, "-p", port, "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes",
                "-o", "ConnectTimeout=10", target, "mkdir", "-p", "yingge-logs"], cancellationToken);
            await RunAsync(scp, ["-q", "-i", keyPath, "-P", port, "-o", "BatchMode=yes",
                "-o", "StrictHostKeyChecking=yes", "-o", "ConnectTimeout=10",
                staged, target + ":yingge-logs/" + filename], cancellationToken);
        }
        finally
        {
            try
            {
                if (File.Exists(staged)) File.Delete(staged);
                Directory.Delete(temporary);
            }
            catch (IOException) { /* The staged bytes remain encrypted. */ }
            catch (UnauthorizedAccessException) { }
        }
        return "~/yingge-logs/" + filename;
    }

    static async Task RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("无法启动 Windows OpenSSH。 ");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(error, output);
            if (process.ExitCode != 0)
                throw new IOException("SSH 传输失败；请检查服务器地址、已记录的主机指纹、密钥权限和远端目录。 ");
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw new IOException("SSH 连接超时或已取消。 ");
        }
    }

    [GeneratedRegex(@"^[a-zA-Z_][a-zA-Z0-9_-]{0,31}@[a-zA-Z0-9][a-zA-Z0-9.-]{0,252}$", RegexOptions.CultureInvariant)]
    private static partial Regex TargetPattern();

}
