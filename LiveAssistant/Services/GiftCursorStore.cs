using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiveAssistant.Services;

/// <summary>
/// 按 web_rid 持久化 im/fetch 的 cursor / internal_ext，支持进程重启恢复。
/// </summary>
public sealed class GiftCursorStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _path;
    private readonly object _gate = new();

    public GiftCursorStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _path = Path.Combine(dataDirectory, "gift_cursors.json");
    }

    public GiftCursorState? Load(string webRid)
    {
        var key = Normalize(webRid);
        if (key == "")
        {
            return null;
        }

        lock (_gate)
        {
            var file = ReadFile();
            return file.Rooms.TryGetValue(key, out var state) ? state : null;
        }
    }

    public void Save(string webRid, string? roomId, string cursor, string internalExt)
    {
        var key = Normalize(webRid);
        if (key == "")
        {
            return;
        }

        lock (_gate)
        {
            var file = ReadFile();
            file.Rooms[key] = new GiftCursorState
            {
                RoomId = roomId ?? "",
                Cursor = cursor ?? "",
                InternalExt = internalExt ?? "",
                UpdatedAt = DateTime.UtcNow.ToString("O")
            };
            WriteFile(file);
        }
    }

    public void Remove(string webRid)
    {
        var key = Normalize(webRid);
        if (key == "")
        {
            return;
        }

        lock (_gate)
        {
            var file = ReadFile();
            if (file.Rooms.Remove(key))
            {
                WriteFile(file);
            }
        }
    }

    private CursorFile ReadFile()
    {
        if (!File.Exists(_path))
        {
            return new CursorFile();
        }

        try
        {
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<CursorFile>(json, JsonOptions) ?? new CursorFile();
        }
        catch
        {
            return new CursorFile();
        }
    }

    private void WriteFile(CursorFile file)
    {
        var json = JsonSerializer.Serialize(file, JsonOptions);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Copy(tmp, _path, overwrite: true);
        File.Delete(tmp);
    }

    private static string Normalize(string webRid) => webRid?.Trim() ?? "";

    private sealed class CursorFile
    {
        [JsonPropertyName("rooms")]
        public Dictionary<string, GiftCursorState> Rooms { get; set; } = new(StringComparer.Ordinal);
    }
}

public sealed class GiftCursorState
{
    [JsonPropertyName("room_id")]
    public string RoomId { get; set; } = "";

    [JsonPropertyName("cursor")]
    public string Cursor { get; set; } = "";

    [JsonPropertyName("internal_ext")]
    public string InternalExt { get; set; } = "";

    [JsonPropertyName("updated_at")]
    public string UpdatedAt { get; set; } = "";
}
