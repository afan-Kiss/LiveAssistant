using NAudio.Wave;

namespace LiveAssistant.Services.AiSpeech;

public static class AudioOutputDevices
{
    public static IReadOnlyList<AudioOutputDeviceInfo> ListDevices()
    {
        var list = new List<AudioOutputDeviceInfo>
        {
            new() { DeviceNumber = -1, Name = WaveOut.DeviceCount > 0
                ? SafeName(-1)
                : "系统默认" }
        };

        for (var i = 0; i < WaveOut.DeviceCount; i++)
        {
            list.Add(new AudioOutputDeviceInfo
            {
                DeviceNumber = i,
                Name = SafeName(i)
            });
        }

        return list;
    }

    public static int ResolveDeviceNumber(string? savedName, int savedNumber)
    {
        var devices = ListDevices();
        if (!string.IsNullOrWhiteSpace(savedName))
        {
            var byName = devices.FirstOrDefault(d =>
                d.DeviceNumber >= 0
                && string.Equals(d.Name, savedName, StringComparison.OrdinalIgnoreCase));
            if (byName != null)
            {
                return byName.DeviceNumber;
            }
        }

        if (savedNumber >= 0 && savedNumber < WaveOut.DeviceCount)
        {
            return savedNumber;
        }

        return -1;
    }

    private static string SafeName(int deviceNumber)
    {
        try
        {
            if (deviceNumber < 0)
            {
                return WaveOut.DeviceCount > 0
                    ? WaveOut.GetCapabilities(0).ProductName + "（默认）"
                    : "系统默认";
            }

            return WaveOut.GetCapabilities(deviceNumber).ProductName;
        }
        catch
        {
            return deviceNumber < 0 ? "系统默认" : $"设备 {deviceNumber}";
        }
    }
}
