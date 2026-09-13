using LiveAssistant.Config;
using LiveAssistant.Database;

namespace LiveAssistant.Services;

public sealed class UserLevelService
{
    private readonly ConfigManager _config;
    private readonly UserRepository _users;

    public UserLevelService(ConfigManager config, UserRepository users)
    {
        _config = config;
        _users = users;
    }

    public int CalculateLevel(int points)
    {
        var thresholds = _config.Settings.UserLevel.Thresholds;
        if (thresholds == null || thresholds.Count == 0)
        {
            return 0;
        }

        var level = 0;
        for (var i = 0; i < thresholds.Count; i++)
        {
            if (points >= thresholds[i])
            {
                level = i;
            }
        }
        return level;
    }

    public void RefreshUserLevel(string userId)
    {
        var user = _users.GetUser(userId);
        if (user == null)
        {
            return;
        }

        var level = CalculateLevel(user.Points);
        if (level != user.Level)
        {
            _users.SetLevel(userId, level);
        }
    }
}
