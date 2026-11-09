using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.HttpOverrides;
using Npgsql;
using Yingge.Cloud.Accounts;
using Yingge.Cloud.Accounts.Api;
using Yingge.Cloud.Accounts.Cli;
using Yingge.Cloud.Accounts.Data;
using Yingge.Cloud.Accounts.Security;
using Yingge.Cloud.Accounts.Services;
using Yingge.Cloud.Accounts.Storage;

// The same binary is the API server and the operator CLI. Database migrations
// and invitation creation are CLI-only: there is no HTTP admin surface to
// attack, and the service account never needs DDL rights.
if (args.Length > 0 && string.Equals(args[0], "admin", StringComparison.Ordinal))
    return await AdminCli.RunAsync(args[1..], CancellationToken.None);

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.AddServerHeader = false;
    kestrel.Limits.MaxRequestBodySize = 256 * 1024;
    kestrel.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
});

// Options are resolved from the final configuration so a host (and the test
// host) can supply settings before they are validated. Startup resolves them
// immediately after Build, so a bad configuration still fails fast.
builder.Services.AddSingleton(serviceProvider => CloudOptions.Load(
    serviceProvider.GetRequiredService<IConfiguration>(),
    serviceProvider.GetRequiredService<IHostEnvironment>()));
builder.Services.AddSingleton(serviceProvider =>
    serviceProvider.GetRequiredService<CloudOptions>().Reports);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(serviceProvider =>
{
    var cloud = serviceProvider.GetRequiredService<CloudOptions>();
    return Db.Create(cloud.Database.AppConnectionString, cloud.Database.PoolMaxSize);
});
builder.Services.AddSingleton(serviceProvider =>
    new ReportFileStore(serviceProvider.GetRequiredService<CloudOptions>().Storage));
builder.Services.AddSingleton(serviceProvider =>
    new PasswordService(serviceProvider.GetRequiredService<CloudOptions>().Security.PasswordIterations));
builder.Services.AddSingleton<AuditRepository>();
builder.Services.AddSingleton<InviteRepository>();
builder.Services.AddSingleton<AccountRepository>();
builder.Services.AddSingleton<AvatarRepository>();
builder.Services.AddSingleton<SessionRepository>();
builder.Services.AddSingleton<ReportRepository>();
builder.Services.AddSingleton<AdminRepository>();
builder.Services.AddSingleton<SessionAuthenticator>();
builder.Services.AddSingleton<ReportService>();
builder.Services.AddSingleton<RetentionService>();
builder.Services.AddHostedService<RetentionWorker>();
builder.Services.AddCloudRateLimiting();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.Configure<RouteHandlerOptions>(route => route.ThrowOnBadRequest = true);
builder.Services.ConfigureHttpJsonOptions(json =>
{
    json.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    json.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    json.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
    json.SerializerOptions.MaxDepth = 8;
});

var app = builder.Build();
var options = app.Services.GetRequiredService<CloudOptions>();
var serverVersion = await Db.ReadServerVersionNumberAsync(
    app.Services.GetRequiredService<NpgsqlDataSource>(), CancellationToken.None);
if (serverVersion < Db.MinimumServerVersionNumber)
    throw new InvalidOperationException(
        $"需要 PostgreSQL 14 或更高版本，当前服务器为 {Db.DescribeVersion(serverVersion)}。");

// Forwarded headers are only honoured for proxies the operator listed. Trusting
// every proxy would let a client spoof its address and defeat IP rate limits.
if (options.Security.KnownProxies.Count > 0)
{
    var forwarded = new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
    };
    forwarded.KnownProxies.Clear();
    forwarded.KnownNetworks.Clear();
    foreach (var proxy in options.Security.KnownProxies) forwarded.KnownProxies.Add(proxy);
    app.UseForwardedHeaders(forwarded);
}

app.UseExceptionHandler();
if (options.Security.RequireHttps) app.UseHsts();
app.Use(async (context, next) =>
{
    if (options.Security.RequireHttps && !context.Request.IsHttps)
    {
        await Problems.Create(StatusCodes.Status426UpgradeRequired, "https_required",
            "本服务只接受 HTTPS 请求。").ExecuteAsync(context);
        return;
    }
    await next();
});
app.UseRateLimiter();

app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] =
        "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self'; " +
        "connect-src 'self'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'";
    if (context.Request.Path.StartsWithSegments("/admin"))
        context.Response.Headers.CacheControl = "no-store";
    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapAuthEndpoints();
app.MapAvatarEndpoints();
app.MapAdminEndpoints();
app.MapReportEndpoints();
app.MapHealthEndpoints();

app.Run();
return 0;

public partial class Program;
