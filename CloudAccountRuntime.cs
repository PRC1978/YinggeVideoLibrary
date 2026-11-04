using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Yingge.Cloud.Client;
using Yingge.Cloud.Protocol;

namespace YinggeVideoLibrary;

// Owns the cloud session for the desktop app. Every method is asynchronous and
// every network failure surfaces as a CloudAccountException that the UI turns
// into a message; nothing here is on the video library's critical path.
internal sealed class CloudAccountRuntime : IDisposable
{
    readonly CloudAccountClient client;

    internal CloudAccountRuntime(CloudAccountSettings settings)
    {
        if (!settings.IsConfigured)
            throw new InvalidOperationException("请先填写 HTTPS 服务器地址。");

        var endpointKey = settings.EndpointKey;
        Outbox = new LocalUploadOutbox(Path.Combine(AppPaths.DataDirectory, "cloud-outbox", endpointKey));
        client = new CloudAccountClient(
            new CloudClientOptions
            {
                BaseAddress = new Uri(settings.ServerUrl.Trim(), UriKind.Absolute),
                ClientVersion = typeof(CloudAccountRuntime).Assembly.GetName().Version?.ToString() ?? "0.0.0",
                Timeout = TimeSpan.FromSeconds(12)
            },
            new WindowsCredentialSessionStore("CloudAccountSession-" + endpointKey));
    }

    internal LocalUploadOutbox Outbox { get; }

    internal bool HasSession => client.HasSession;

    internal Task<RegistrationResponse> RegisterAsync(string inviteCode, string username, string password,
        CancellationToken ct) => client.RegisterAsync(inviteCode, username, password, ct);

    internal Task<AuthResponse> LoginAsync(string username, string password, CancellationToken ct) =>
        client.LoginAsync(username, password, ct);

    internal Task<LogoutResult> LogoutAsync(CancellationToken ct) => client.LogoutAsync(ct);

    internal Task<SessionStatus?> GetStatusAsync(CancellationToken ct) => client.GetSessionAsync(ct);

    internal Task<ErrorReportReceipt> UploadAsync(ErrorReportDraft draft, CancellationToken ct) =>
        client.UploadReportAsync(draft, ct);

    internal Task<OutboxFlushResult> FlushOutboxAsync(CancellationToken ct) => Outbox.FlushAsync(client, ct);

    internal Task<IReadOnlyList<ErrorReportSummary>> ListReportsAsync(CancellationToken ct) =>
        client.ListReportsAsync(ct);

    internal Task DeleteReportAsync(Guid id, CancellationToken ct) => client.DeleteReportAsync(id, ct);

    public void Dispose() => client.Dispose();
}
