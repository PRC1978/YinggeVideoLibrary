using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace YinggeVideoLibrary;

// Download only the official redirect target. The archive stays in the folder
// chosen by the user; import is a separate step and never replaces a live
// catalogue until its own validation succeeds.
internal static class R18OfflineDownload
{
    internal const long MaxBytes = 2L * 1024 * 1024 * 1024;
    static readonly Uri Latest = new("https://r18.dev/dumps/latest");
    static readonly Regex ArchivePath = new(
        @"^/dumps/(?<name>r18dotdev_dump_(?<date>\d{4}-\d{2}-\d{2})\.sql\.gz)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal sealed record DownloadInfo(Uri Url, string FileName);
    internal sealed record DownloadProgress(long BytesReceived, long? TotalBytes);

    internal static HttpClient CreateClient() => new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false
    }) { Timeout = Timeout.InfiniteTimeSpan };

    internal static DownloadInfo? ValidateTarget(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps ||
            uri.Host != "r18dotdev.s3.eu-west-1.wasabisys.com" || uri.Port != 443 ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            return null;
        var match = ArchivePath.Match(uri.AbsolutePath);
        if (!match.Success || !DateOnly.TryParseExact(match.Groups["date"].Value,
                "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            return null;
        return new DownloadInfo(uri, match.Groups["name"].Value);
    }

    internal static async Task<DownloadInfo> ResolveLatestAsync(HttpClient client, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Get, Latest);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or
            HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect))
            throw new InvalidDataException("r18.dev 未返回官方数据包下载地址，请稍后重试。");
        var target = ValidateTarget(response.Headers.Location);
        return target ?? throw new InvalidDataException("r18.dev 返回的数据包地址不在官方存储位置，已停止下载。");
    }

    internal static async Task<string> DownloadAsync(HttpClient client, DownloadInfo info, string folder,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        if (ValidateTarget(info.Url) is not { } verified || verified.FileName != info.FileName)
            throw new InvalidDataException("下载地址或文件名不符合官方格式。");
        var directory = new DirectoryInfo(folder);
        if (!directory.Exists) throw new DirectoryNotFoundException("保存文件夹不存在。");
        var target = Path.Combine(directory.FullName, info.FileName);
        if (File.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("目标文件不能是文件链接。");
        var temporary = target + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            using var request = new HttpRequestMessage(HttpMethod.Get, info.Url);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            timeout.CancelAfter(Timeout.InfiniteTimeSpan);
            if (response.StatusCode != HttpStatusCode.OK || response.RequestMessage?.RequestUri != info.Url)
                throw new InvalidDataException("官方数据包下载未成功，或发生了未允许的跳转。");
            var total = response.Content.Headers.ContentLength;
            if (total is > MaxBytes or < 1024)
                throw new InvalidDataException("官方数据包大小不在允许范围内。");
            var available = new DriveInfo(Path.GetPathRoot(directory.FullName)!).AvailableFreeSpace;
            if (available < (total ?? 512L * 1024 * 1024) + 64L * 1024 * 1024)
                throw new IOException("保存位置空间不足，请换一个文件夹。");
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[128 * 1024];
            long received = 0;
            long nextReport = 0;
            while (true)
            {
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                readTimeout.CancelAfter(TimeSpan.FromSeconds(45));
                var count = await source.ReadAsync(buffer, readTimeout.Token);
                if (count == 0) break;
                received += count;
                if (received > MaxBytes) throw new InvalidDataException("数据包超过 2 GB 上限，已停止下载。");
                await destination.WriteAsync(buffer.AsMemory(0, count), ct);
                if (received >= nextReport || received == total)
                {
                    progress?.Report(new DownloadProgress(received, total));
                    nextReport = received + 1024 * 1024;
                }
            }
            if (received < 1024 || total is not null && received != total)
                throw new InvalidDataException("数据包下载不完整。");
            await destination.FlushAsync(ct);
            destination.Close();
            using (var probe = File.OpenRead(temporary))
            {
                if (probe.ReadByte() != 0x1f || probe.ReadByte() != 0x8b)
                    throw new InvalidDataException("下载内容不是 gzip 数据包。");
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, target, true);
            return target;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

internal static class R18OfflineDownloadDiagnostics
{
    sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request));
        }
    }

    internal static async Task<IReadOnlyDictionary<string, bool>> VerifyAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var target = new Uri("https://r18dotdev.s3.eu-west-1.wasabisys.com/dumps/r18dotdev_dump_2026-11-03.sql.gz");
        R18OfflineDownload.DownloadInfo info = R18OfflineDownload.ValidateTarget(target)
            ?? throw new InvalidOperationException("诊断用官方数据包地址无效。");
        var checks = new Dictionary<string, bool>
        {
            ["r18DownloadAcceptsOfficialArchive"] = info.FileName == "r18dotdev_dump_2026-11-03.sql.gz",
            ["r18DownloadRejectsOtherHosts"] = R18OfflineDownload.ValidateTarget(new Uri(
                "https://r18dotdev.s3.eu-west-1.wasabisys.com.evil.invalid/dumps/r18dotdev_dump_2026-11-03.sql.gz")) is null,
            ["r18DownloadRejectsQueryAndWrongName"] = R18OfflineDownload.ValidateTarget(new Uri(target + "?token=bad")) is null &&
                R18OfflineDownload.ValidateTarget(new Uri("https://r18dotdev.s3.eu-west-1.wasabisys.com/dumps/other.sql.gz")) is null
        };
        using (var redirectClient = new HttpClient(new StubHandler(request => new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
               { RequestMessage = request, Headers = { Location = target } })))
        {
            checks["r18DownloadResolvesOfficialRedirect"] =
                await R18OfflineDownload.ResolveLatestAsync(redirectClient, CancellationToken.None) == info;
        }
        using (var maliciousClient = new HttpClient(new StubHandler(request => new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
               { RequestMessage = request, Headers = { Location = new Uri("https://evil.invalid/dump.sql.gz") } })))
        {
            try
            {
                await R18OfflineDownload.ResolveLatestAsync(maliciousClient, CancellationToken.None);
                checks["r18DownloadRejectsUntrustedRedirect"] = false;
            }
            catch (InvalidDataException) { checks["r18DownloadRejectsUntrustedRedirect"] = true; }
        }
        var data = new byte[2048];
        new Random(12345).NextBytes(data);
        byte[] archive;
        using (var output = new MemoryStream())
        {
            using (var gzip = new GZipStream(output, CompressionLevel.NoCompression, leaveOpen: true))
                gzip.Write(data);
            archive = output.ToArray();
        }
        using (var downloadClient = new HttpClient(new StubHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
               { RequestMessage = request, Content = new ByteArrayContent(archive) })))
        {
            var saved = await R18OfflineDownload.DownloadAsync(downloadClient, info, directory, null, CancellationToken.None);
            checks["r18DownloadWritesSelectedFolder"] = saved == Path.Combine(directory, info.FileName) &&
                File.ReadAllBytes(saved).SequenceEqual(archive);
        }
        using (var brokenClient = new HttpClient(new StubHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
               { RequestMessage = request, Content = new ByteArrayContent(new byte[1100]) })))
        {
            try
            {
                await R18OfflineDownload.DownloadAsync(brokenClient, info, directory, null, CancellationToken.None);
                checks["r18DownloadFailureKeepsExistingArchive"] = false;
            }
            catch (InvalidDataException)
            {
                checks["r18DownloadFailureKeepsExistingArchive"] =
                    File.ReadAllBytes(Path.Combine(directory, info.FileName)).SequenceEqual(archive) &&
                    !Directory.EnumerateFiles(directory, "*.partial-*").Any();
            }
        }
        return checks;
    }
}
