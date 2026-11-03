using System.Text.Json;
using System.Text.Json.Nodes;

// Independent protocol example: no reference to the host assembly. A real
// local model plugin replaces only the fixed response below.
while (Console.ReadLine() is { } line)
{
    try
    {
        var request = JsonNode.Parse(line);
        var id = request?["id"]?.GetValue<string>() ?? "";
        var method = request?["method"]?.GetValue<string>() ?? "";
        var prompt = request?["aiPayload"]?["messages"]?[0]?["content"]?.GetValue<string>() ?? "";
        if (method == "ai.complete" && prompt == "slow") Thread.Sleep(5000);
        JsonNode result = method switch
        {
            "describe" => new JsonObject { ["name"] = "示例本机 AI 与只读工具",
                ["version"] = "1.0.0", ["capabilities"] = new JsonArray("ai.service", "tool.command") },
            "ai.complete" when prompt == "invalid" => new JsonObject(),
            "ai.complete" => new JsonObject {
                ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject {
                    ["content"] = "{\"verdict\":\"reject\",\"confidence\":0,\"reason\":\"离线协议示例不判断作品身份\"}" } }),
                ["usage"] = new JsonObject { ["prompt_tokens"] = 8, ["completion_tokens"] = 8 } },
            "tool.run" => new JsonObject { ["text"] = request?["params"]?["query"]?.GetValue<string>() == "invalidtool"
                ? new string('x', 2200) : "离线只读工具：已收到视频显示标题；未修改任何资料。" },
            _ => throw new InvalidOperationException("unsupported method")
        };
        Console.WriteLine(new JsonObject { ["v"] = 1, ["id"] = id, ["ok"] = true,
            ["result"] = result }.ToJsonString());
    }
    catch (Exception ex) when (ex is JsonException or InvalidOperationException)
    {
        Console.Error.WriteLine("request rejected");
    }
}
