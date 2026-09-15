using LiveAssistant.Config;

namespace LiveAssistant.Services.AiSpeech;

/// <summary>
/// 从 Config/ai_personality.txt 加载主播人格；用户可直接编辑。
/// </summary>
public static class AiPersonalityLoader
{
    public const string FileName = "ai_personality.txt";

    public static string DefaultPersonalityText =>
        """
        你是一名真实直播主播。

        说话自然，像朋友聊天。

        不要客服。
        不要长篇。
        不要说「团队」「作为AI」。

        提到功能/软件时，就说是自己慢慢做的、还在继续改。

        回复10~40字。

        可以使用：

        嗯
        对
        这个确实
        哈哈

        但不要每句都有。
        """.Replace("\r\n", "\n").Trim() + "\n";

    public static string GetConfigPath()
        => Path.Combine(AppPaths.ConfigDirectory, FileName);

    public static string GetDataPath(string dataDirectory)
        => Path.Combine(dataDirectory, FileName);

    /// <summary>
    /// 优先 data/，其次 Config/；缺失时写入默认人格到 Config/。
    /// </summary>
    public static string Load(string dataDirectory, out string sourcePath)
    {
        EnsureDefaultFileExists();

        var dataPath = GetDataPath(dataDirectory);
        if (TryRead(dataPath, out var dataText))
        {
            sourcePath = dataPath;
            return dataText;
        }

        var configPath = GetConfigPath();
        if (TryRead(configPath, out var configText))
        {
            sourcePath = configPath;
            return configText;
        }

        sourcePath = configPath;
        return DefaultPersonalityText.Trim();
    }

    public static string LoadOrFallback(string dataDirectory, string? settingsFallback)
    {
        var text = Load(dataDirectory, out _);
        if (!string.IsNullOrWhiteSpace(text))
        {
            return text.Trim();
        }

        if (!string.IsNullOrWhiteSpace(settingsFallback))
        {
            return settingsFallback.Trim();
        }

        return DefaultPersonalityText.Trim();
    }

    public static void EnsureDefaultFileExists()
    {
        try
        {
            var dir = AppPaths.ConfigDirectory;
            Directory.CreateDirectory(dir);
            var path = GetConfigPath();
            if (!File.Exists(path))
            {
                File.WriteAllText(path, DefaultPersonalityText);
            }
        }
        catch
        {
            // 忽略：人格文件写失败时走内置默认
        }
    }

    private static bool TryRead(string path, out string text)
    {
        text = "";
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            text = File.ReadAllText(path).Trim();
            return text.Length > 0;
        }
        catch
        {
            return false;
        }
    }
}
