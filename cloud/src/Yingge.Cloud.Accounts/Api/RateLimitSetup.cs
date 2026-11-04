using System.Security.Cryptography;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Yingge.Cloud.Protocol;

namespace Yingge.Cloud.Accounts.Api;

internal static class RateLimitSetup
{
    public const string Login = "login";
    public const string Register = "register";
    public const string Upload = "upload";

    public static IServiceCollection AddCloudRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                RateLimitPartition.GetFixedWindowLimiter(RemoteKey(context), _ =>
                    new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = context.RequestServices.GetRequiredService<CloudOptions>()
                            .Security.GlobalPerMinute,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                        AutoReplenishment = true
                    }));

            limiter.AddPolicy(Login, context =>
                Fixed(context, cloud => cloud.Security.LoginPerMinute, TimeSpan.FromMinutes(1)));

            limiter.AddPolicy(Register, context =>
                Fixed(context, cloud => cloud.Security.RegisterPerHour, TimeSpan.FromHours(1)));

            // Uploads are limited per session token, so one misbehaving client
            // cannot exhaust the allowance of every user behind the same NAT.
            limiter.AddPolicy(Upload, context =>
                Fixed(context, cloud => Math.Max(2, cloud.Reports.BurstLimit + 1),
                    TimeSpan.FromMinutes(1), perSession: true));

            limiter.OnRejected = async (context, token) =>
            {
                var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var window)
                    ? Math.Max(1, (int)Math.Ceiling(window.TotalSeconds))
                    : 60;
                await Problems.RateLimited("请求过于频繁，请稍后再试。", retryAfter)
                    .ExecuteAsync(context.HttpContext);
            };
        });

        return services;
    }

    static RateLimitPartition<string> Fixed(HttpContext context, Func<CloudOptions, int> permitLimit,
        TimeSpan window, bool perSession = false)
    {
        var key = perSession ? SessionKey(context) : RemoteKey(context);
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitLimit(context.RequestServices.GetRequiredService<CloudOptions>()),
            Window = window,
            QueueLimit = 0,
            AutoReplenishment = true
        });
    }

    static string RemoteKey(HttpContext context) =>
        "ip:" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown");

    static string SessionKey(HttpContext context)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(authorization)) return RemoteKey(context);
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(authorization));
        return "token:" + Convert.ToHexString(hash)[..16];
    }
}
