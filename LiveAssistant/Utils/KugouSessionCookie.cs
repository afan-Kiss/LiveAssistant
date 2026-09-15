using LiveAssistant.Models;

namespace LiveAssistant.Utils;

internal static class KugouSessionCookie
{
    /// <summary>对齐酷狗侧车 session.json / moeGET 的 cookie 格式。</summary>
    public static string BuildHeader(KugouSidecarSession? session)
    {
        if (session == null || string.IsNullOrWhiteSpace(session.Token) || string.IsNullOrWhiteSpace(session.UserId))
        {
            return "";
        }

        var parts = new List<string>(8);
        if (!string.IsNullOrWhiteSpace(session.Dfid))
        {
            parts.Add("dfid=" + session.Dfid.Trim());
        }

        parts.Add("token=" + session.Token.Trim());
        parts.Add("userid=" + session.UserId.Trim());

        if (!string.IsNullOrWhiteSpace(session.VipToken))
        {
            parts.Add("vip_token=" + session.VipToken.Trim());
        }

        if (!string.IsNullOrWhiteSpace(session.VipType))
        {
            parts.Add("vip_type=" + session.VipType.Trim());
        }

        if (session.Extra != null)
        {
            foreach (var pair in session.Extra)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
                {
                    continue;
                }

                parts.Add(pair.Key.Trim() + "=" + pair.Value.Trim());
            }
        }

        return string.Join(";", parts);
    }
}
