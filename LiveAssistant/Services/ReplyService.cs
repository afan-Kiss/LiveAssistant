using LiveAssistant.Config;

namespace LiveAssistant.Services;

public sealed class ReplyService
{
    private readonly ConfigManager _config;

    public ReplyService(ConfigManager config)
    {
        _config = config;
    }

    public string Render(string templateKey, IDictionary<string, string> variables)
    {
        if (!_config.ReplyTemplates.TryGetValue(templateKey, out var template) || string.IsNullOrWhiteSpace(template))
        {
            return "";
        }

        var result = template;
        foreach (var (key, value) in variables)
        {
            result = result.Replace($"{{{key}}}", value ?? "", StringComparison.OrdinalIgnoreCase);
        }
        return result;
    }

    public void Reload()
    {
        _config.Load();
    }
}
