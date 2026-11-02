using System.Security.Cryptography;
using System.Text;

namespace YinggeVideoLibrary;

internal readonly record struct AiScrapeTrace(string AssessmentId, string RequestSha256,
    string ResponseSha256, string EvidenceSchema)
{
    internal static AiScrapeTrace Create(string jobId, string requestJson, string responseText) => new(
        jobId, Hash(requestJson), Hash(responseText),
        "yingge.ai-scrape-evidence.v1");

    static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
