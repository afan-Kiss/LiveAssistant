using LiveAssistant.Config;
using LiveAssistant.Database;

namespace LiveAssistant.Services;

/// <summary>
/// 回复模板预览（不发送弹幕）。
/// </summary>
public sealed class ReplyTemplatePreviewService
{
    private readonly ConfigManager _config;
    private readonly ReplyTemplateRepository _templates;
    private readonly ReplyService _reply;

    public ReplyTemplatePreviewService(
        ConfigManager config,
        ReplyTemplateRepository templates,
        ReplyService reply)
    {
        _config = config;
        _templates = templates;
        _reply = reply;
    }

    public ReplyTemplatePreviewResult Preview(string templateKey, string testUser, string? templateContent = null)
    {
        var content = templateContent;
        if (string.IsNullOrWhiteSpace(content))
        {
            if (!_config.ReplyTemplates.TryGetValue(templateKey, out content) || string.IsNullOrWhiteSpace(content))
            {
                var fromDb = _templates.GetAll();
                content = fromDb.GetValueOrDefault(templateKey) ?? "";
            }
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            return new ReplyTemplatePreviewResult
            {
                Ok = false,
                Message = $"模板 {templateKey} 不存在或内容为空"
            };
        }

        var vars = BuildSampleVariables(testUser);
        var rendered = RenderContent(content, vars);
        return new ReplyTemplatePreviewResult
        {
            Ok = true,
            TemplateKey = templateKey,
            TestUser = testUser,
            RawTemplate = content,
            PreviewText = rendered,
            VariablesUsed = vars
        };
    }

    private static Dictionary<string, string> BuildSampleVariables(string testUser)
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = string.IsNullOrWhiteSpace(testUser) ? "测试用户" : testUser,
            ["song"] = "晴天",
            ["artist"] = "周杰伦",
            ["queue"] = "3",
            ["gift"] = "小心心",
            ["count"] = "1",
            ["score"] = "100",
            ["level"] = "5",
            ["cost"] = "10",
            ["reason"] = "示例原因",
            ["seconds"] = "30",
            ["artists"] = "周杰伦 / 林俊杰 / 五月天",
            ["details"] = "+50 礼物奖励 / -10 点歌消费"
        };
    }

    private static string RenderContent(string template, IDictionary<string, string> variables)
    {
        var result = template;
        foreach (var (key, value) in variables)
        {
            result = result.Replace($"{{{key}}}", value ?? "", StringComparison.OrdinalIgnoreCase);
        }

        return result;
    }
}

public sealed class ReplyTemplatePreviewResult
{
    public bool Ok { get; set; }
    public string? Message { get; set; }
    public string TemplateKey { get; set; } = "";
    public string TestUser { get; set; } = "";
    public string RawTemplate { get; set; } = "";
    public string PreviewText { get; set; } = "";
    public Dictionary<string, string>? VariablesUsed { get; set; }
}
