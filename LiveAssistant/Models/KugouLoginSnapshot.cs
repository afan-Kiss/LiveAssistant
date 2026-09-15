namespace LiveAssistant.Models;

public sealed class KugouLoginSnapshot
{
    public bool LoggedIn { get; init; }
    public string UserId { get; init; } = "";
    public string Nickname { get; init; } = "";
    public string VipLabel { get; init; } = "";
    public string VipType { get; init; } = "";
    public bool HasVipToken { get; init; }
    public string VipEnd { get; init; } = "";

    public string DisplayStatus
    {
        get
        {
            if (!LoggedIn)
            {
                return "未登录";
            }

            var name = string.IsNullOrWhiteSpace(Nickname) ? "已登录" : Nickname;
            if (string.IsNullOrWhiteSpace(VipLabel))
            {
                return name;
            }

            return $"{name} · {VipLabel}";
        }
    }
}
