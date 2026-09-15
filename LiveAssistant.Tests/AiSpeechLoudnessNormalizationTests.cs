using System.Text;
using LiveAssistant.Services.AiSpeech;
using Xunit;

namespace LiveAssistant.Tests;

public class AiSpeechLoudnessNormalizationTests
{
    private static byte[] BuildSineWav(float amplitude, int sampleRate = 8000, double seconds = 0.08)
    {
        var n = (int)(sampleRate * seconds);
        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true))
        {
            var dataBytes = n * 2;
            bw.Write(Encoding.ASCII.GetBytes("RIFF"));
            bw.Write(36 + dataBytes);
            bw.Write(Encoding.ASCII.GetBytes("WAVE"));
            bw.Write(Encoding.ASCII.GetBytes("fmt "));
            bw.Write(16);
            bw.Write((short)1);
            bw.Write((short)1);
            bw.Write(sampleRate);
            bw.Write(sampleRate * 2);
            bw.Write((short)2);
            bw.Write((short)16);
            bw.Write(Encoding.ASCII.GetBytes("data"));
            bw.Write(dataBytes);
            for (var i = 0; i < n; i++)
            {
                var sample = (short)(amplitude * short.MaxValue * Math.Sin(2 * Math.PI * 440 * i / sampleRate));
                bw.Write(sample);
            }
        }

        return ms.ToArray();
    }

    [Fact]
    public void QuietWav_IsAutoBoosted()
    {
        var quiet = BuildSineWav(0.05f);
        var plan = AiSpeechPlayer.AnalyzeBytesFullChain(quiet, userGain: 1.0f);
        Assert.True(plan.PeakBefore < 0.1f, $"peak_before={plan.PeakBefore}");
        Assert.True(plan.NormalizeGain > 5f, $"normalize_gain={plan.NormalizeGain}");
        Assert.True(plan.NormalizeGainDb > 12f, $"normalize_db={plan.NormalizeGainDb}");
        Assert.True(plan.EstimatedPeakAfter > 0.5f, $"peak_after={plan.EstimatedPeakAfter}");
        Assert.True(plan.EstimatedPeakAfter <= 1.0001f);
    }

    [Fact]
    public void NormalWav_IsNotOverAmplified()
    {
        var normal = BuildSineWav(0.55f);
        var plan = AiSpeechPlayer.AnalyzeBytesFullChain(normal, userGain: 1.0f);
        Assert.True(plan.PeakBefore > 0.4f);
        Assert.True(plan.NormalizeGain < 2.5f, $"normalize should be mild: {plan.NormalizeGain}");
        Assert.True(plan.NormalizeGainDb < 8f, $"normalize_db={plan.NormalizeGainDb}");
        Assert.InRange(plan.EstimatedPeakAfter, 0.5f, 1.0001f);
    }

    [Fact]
    public void UserVolume_100_150_180_200_AllTakeEffect()
    {
        var wav = BuildSineWav(0.2f);
        var p100 = AiSpeechPlayer.AnalyzeBytesFullChain(wav, 1.0f);
        var p150 = AiSpeechPlayer.AnalyzeBytesFullChain(wav, 1.5f);
        var p180 = AiSpeechPlayer.AnalyzeBytesFullChain(wav, 1.8f);
        var p200 = AiSpeechPlayer.AnalyzeBytesFullChain(wav, 2.0f);

        Assert.Equal(1.0f, p100.UserGain);
        Assert.Equal(1.5f, p150.UserGain);
        Assert.Equal(1.8f, p180.UserGain);
        Assert.Equal(2.0f, p200.UserGain);

        // 同一归一化基础上，用户增益越大，限幅前 combined 越大
        Assert.True(p150.CombinedGain > p100.CombinedGain);
        Assert.True(p180.CombinedGain > p150.CombinedGain);
        Assert.True(p200.CombinedGain > p180.CombinedGain);

        // 限幅后仍应单调不降（软限幅下至少不反转）
        Assert.True(p150.EstimatedPeakAfter + 1e-4f >= p100.EstimatedPeakAfter * 0.95f);
        Assert.True(p200.EstimatedPeakAfter <= 1.0001f);
    }

    [Fact]
    public void Volume200_DoesNotHardClipBeyondUnity()
    {
        var loudish = BuildSineWav(0.4f);
        var plan = AiSpeechPlayer.AnalyzeBytesFullChain(loudish, 2.0f);
        Assert.True(plan.EstimatedPeakAfter <= 1.0001f, $"peak_after={plan.EstimatedPeakAfter}");
        Assert.True(SoftLimitingSampleProvider.SoftLimit(3f) <= 1f);
    }

    [Fact]
    public async Task Play_TwentyTimes_NoTempLeak()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ai-loud-leak-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var wav = BuildSineWav(0.2f, seconds: 0.03);
        using var player = new AiSpeechPlayer();
        player.SetVolumePercent(180);
        try
        {
            for (var i = 0; i < 20; i++)
            {
                await player.PlayWavAsync(wav, dir, TimeSpan.FromSeconds(5), CancellationToken.None);
                Assert.Null(player.CurrentTempFile);
                Assert.False(player.IsPlaying);
            }

            Assert.Empty(Directory.EnumerateFiles(dir, "*.wav"));
        }
        finally
        {
            AiSpeechPlayer.CleanupTempDirectory(dir);
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void DefaultVolumePercent_Is180()
    {
        var s = new LiveAssistant.Config.AiSpeechSettings();
        Assert.Equal(180, s.VolumePercent);
    }

    [Fact]
    public void TargetPeak_IsAboutMinusOneDbFs()
    {
        var db = AiSpeechPlayer.LinearToDb(AiSpeechPlayer.TargetPeakLinear);
        Assert.InRange(db, -1.05f, -0.95f);
    }
}
