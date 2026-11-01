using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ExampleScrapePlugin;

// Reference implementation of the Yingge scrape-source protocol (API version 1).
//
// The host sends one JSON request per line on stdin and expects exactly one
// JSON response line on stdout, matched by "id". Diagnostics go to stderr.
// This sample never touches the network; inject the query keywords below to
// exercise the host's failure handling.
internal static class Program
{
    const int ApiVersion = 1;

    static int Main()
    {
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);
        string? line;
        while ((line = Console.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonNode? request;
            try { request = JsonNode.Parse(line); }
            catch (JsonException)
            {
                Console.Error.WriteLine("ignored: request is not JSON");
                continue;
            }
            var id = request?["id"]?.GetValue<string>() ?? "";
            var method = request?["method"]?.GetValue<string>() ?? "";
            switch (method)
            {
                case "describe":
                    Respond(new JsonObject
                    {
                        ["v"] = ApiVersion, ["id"] = id, ["ok"] = true,
                        ["result"] = new JsonObject
                        {
                            ["name"] = "示例搜刮来源",
                            ["version"] = "1.0.0",
                            ["capabilities"] = new JsonArray("scrape.source")
                        }
                    });
                    break;
                case "search":
                    Search(id, request?["params"]);
                    break;
                default:
                    Error(id, "unknown_method", $"unsupported method: {method}");
                    break;
            }
        }
        return 0;
    }

    static void Search(string id, JsonNode? parameters)
    {
        var query = parameters?["query"]?.GetValue<string>() ?? "";
        var limit = parameters?["limit"]?.GetValue<int>() ?? 8;
        limit = Math.Clamp(limit, 1, 20);

        // Failure modes are driven by the query so the host self-test can
        // reproduce them without extra switches.
        if (query.Contains("slow", StringComparison.OrdinalIgnoreCase))
        {
            Thread.Sleep(TimeSpan.FromSeconds(90));
            return;
        }
        if (query.Contains("crash", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("simulated crash");
            Environment.Exit(7);
        }
        if (query.Contains("huge", StringComparison.OrdinalIgnoreCase))
        {
            Console.Out.WriteLine(new string('x', 400 * 1024));
            Console.Out.Flush();
            return;
        }
        if (query.Contains("ratelimit", StringComparison.OrdinalIgnoreCase))
        {
            Error(id, "rate_limited", "示例插件模拟限流", retryAfterSeconds: 900);
            return;
        }
        if (query.Contains("pathleak", StringComparison.OrdinalIgnoreCase))
        {
            var leak = new JsonArray
            {
                new JsonObject
                {
                    ["title"] = @"C://Users//someone//Videos//private.mp4",
                    ["source"] = "示例来源",
                    ["sourceUrl"] = "https://example.com/leak",
                    ["confidenceHint"] = 60
                }
            };
            Respond(new JsonObject
            {
                ["v"] = ApiVersion, ["id"] = id, ["ok"] = true,
                ["result"] = new JsonObject { ["candidates"] = leak }
            });
            return;
        }
        if (query.Contains("empty", StringComparison.OrdinalIgnoreCase))
        {
            Respond(new JsonObject
            {
                ["v"] = ApiVersion, ["id"] = id, ["ok"] = true,
                ["result"] = new JsonObject { ["candidates"] = new JsonArray() }
            });
            return;
        }

        var candidates = new JsonArray();
        for (var index = 1; index <= Math.Min(limit, 3); index++)
        {
            candidates.Add(new JsonObject
            {
                ["title"] = $"{query} 示例作品 {index}",
                ["originalTitle"] = $"Example Work {index}",
                ["code"] = $"EX-{index:000}",
                ["year"] = 2023 + index,
                ["source"] = "示例来源",
                ["sourceUrl"] = $"https://example.com/work/{index}?q={Uri.EscapeDataString(query)}",
                ["posterUrl"] = $"https://example.com/poster/{index}.jpg",
                ["summary"] = "由示例插件返回的合成资料，仅用于验收。",
                ["evidence"] = "示例插件按标题生成的合成候选，没有真实页面依据",
                ["confidenceHint"] = 55
            });
        }
        Respond(new JsonObject
        {
            ["v"] = ApiVersion, ["id"] = id, ["ok"] = true,
            ["result"] = new JsonObject { ["candidates"] = candidates }
        });
    }

    static void Error(string id, string code, string message, int? retryAfterSeconds = null)
    {
        var error = new JsonObject { ["code"] = code, ["message"] = message };
        if (retryAfterSeconds is { } seconds) error["retryAfterSeconds"] = seconds;
        Respond(new JsonObject { ["v"] = ApiVersion, ["id"] = id, ["ok"] = false, ["error"] = error });
    }

    static void Respond(JsonObject payload)
    {
        Console.Out.WriteLine(payload.ToJsonString());
        Console.Out.Flush();
    }
}
