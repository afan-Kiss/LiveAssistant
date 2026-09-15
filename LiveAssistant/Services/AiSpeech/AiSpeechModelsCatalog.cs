namespace LiveAssistant.Services.AiSpeech;

/// <summary>
/// AI 互动模型目录：推荐列表可自由选择，不写死单一模型。
/// </summary>
public static class AiSpeechModelsCatalog
{
    public const string DefaultModel = "qwen3:8b";

    /// <summary>推荐互动模型（体积小到大）；27b 仅保留可选，不适合实时默认。</summary>
    public static readonly string[] RecommendedModels =
    {
        "qwen3:8b",
        "qwen2.5:7b",
        "qwen3.5:27b"
    };

    public static bool IsRecommended(string? model)
        => !string.IsNullOrWhiteSpace(model)
           && RecommendedModels.Any(m => m.Equals(model.Trim(), StringComparison.OrdinalIgnoreCase));

    public static string FormatDisplayName(string model)
    {
        model = (model ?? "").Trim();
        if (model.Length == 0)
        {
            return "";
        }

        if (model.Equals(DefaultModel, StringComparison.OrdinalIgnoreCase))
        {
            return $"{model}（推荐）";
        }

        if (model.Equals("qwen3.5:27b", StringComparison.OrdinalIgnoreCase))
        {
            return $"{model}（大模型，易占显存）";
        }

        return model;
    }

    /// <summary>推荐模型优先，再合并本机已安装模型，去重保序。</summary>
    public static List<string> MergeWithInstalled(IEnumerable<string>? installed)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0 || !seen.Add(name))
            {
                return;
            }

            result.Add(name);
        }

        foreach (var m in RecommendedModels)
        {
            Add(m);
        }

        if (installed != null)
        {
            foreach (var m in installed)
            {
                Add(m);
            }
        }

        return result;
    }

    public static string ResolveDefault(string? configured, IReadOnlyList<string>? available = null)
    {
        var model = (configured ?? "").Trim();
        if (model.Length > 0)
        {
            return model;
        }

        if (available != null)
        {
            var hit = available.FirstOrDefault(m =>
                m.Equals(DefaultModel, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(hit))
            {
                return hit;
            }

            foreach (var rec in RecommendedModels)
            {
                hit = available.FirstOrDefault(m => m.Equals(rec, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(hit))
                {
                    return hit;
                }
            }

            if (available.Count > 0)
            {
                return available[0];
            }
        }

        return DefaultModel;
    }
}
