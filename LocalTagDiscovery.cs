using System.Text;

namespace YinggeVideoLibrary;

// Reuses labels already present in this library. An exact metadata value or an
// exact filename/folder term is evidence; a fuzzy substring is not.
internal static class LocalTagDiscovery
{
    internal sealed record Input(string VideoId, string Title, string Folder,
        string[] CurrentTags, string[] SourceGenres, string[] SourceKeywords, bool Verified);
    internal sealed record Suggestion(string VideoId, string Title, string[] Tags, string Evidence);

    internal static List<Suggestion> Build(IReadOnlyList<Input> selected, IEnumerable<string> existingTags)
    {
        var vocabulary = existingTags.Where(Eligible).Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToDictionary(tag => tag, tag => tag, StringComparer.CurrentCultureIgnoreCase);
        var result = new List<Suggestion>();
        foreach (var video in selected)
        {
            var current = video.CurrentTags.ToHashSet(StringComparer.CurrentCultureIgnoreCase);
            var found = new Dictionary<string, string>(StringComparer.CurrentCultureIgnoreCase);
            void Add(string candidate, string evidence)
            {
                if (vocabulary.TryGetValue(candidate.Trim(), out var canonical) && !current.Contains(canonical))
                    found.TryAdd(canonical, evidence);
            }
            if (video.Verified)
            {
                foreach (var genre in video.SourceGenres) Add(genre, "已核实作品类型");
                foreach (var keyword in video.SourceKeywords) Add(keyword, "已核实作品关键词");
            }
            foreach (var term in Terms(video.Title)) Add(term, "文件标题");
            foreach (var segment in video.Folder.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).TakeLast(3))
                foreach (var term in Terms(segment)) Add(term, "文件夹名");
            if (found.Count == 0) continue;
            var chosen = found.Take(8).ToArray();
            result.Add(new Suggestion(video.VideoId, video.Title,
                chosen.Select(pair => pair.Key).ToArray(),
                string.Join("；", chosen.Select(pair => pair.Key + "：" + pair.Value))));
        }
        return result;
    }

    static bool Eligible(string? tag) => tag is { Length: >= 2 and <= 24 } &&
        !string.IsNullOrWhiteSpace(tag) && tag != "素人" && tag != "未分类" &&
        !tag.Any(char.IsControl);

    static IEnumerable<string> Terms(string text)
    {
        var term = new StringBuilder();
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c)) term.Append(c);
            else if (term.Length > 0)
            {
                yield return term.ToString();
                term.Clear();
            }
        }
        if (term.Length > 0) yield return term.ToString();
    }

    internal static bool VerifyForDiagnostics()
    {
        var inputs = new[]
        {
            new Input("one", "科幻 作品A.mp4", @"C:\片库\日本\导演甲", [], ["科幻"], ["悬疑"], true),
            new Input("two", "别的作品.mp4", @"C:\片库\人物乙", ["人物乙"], ["科幻"], [], false),
            new Input("three", "科幻片段.mp4", @"C:\片库\素人", [], [], [], false)
        };
        var found = Build(inputs, ["科幻", "悬疑", "导演甲", "人物乙", "素人"]);
        return found.Count == 1 && found[0].VideoId == "one" &&
            found[0].Tags.SequenceEqual(["科幻", "悬疑", "导演甲"]) &&
            !found.SelectMany(item => item.Tags).Contains("素人");
    }
}
