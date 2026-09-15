using LiveAssistant;
using Xunit;

namespace LiveAssistant.Tests;

public sealed class SidecarLocatorTests
{
    [Fact]
    public void SameDirectory_PrefersLocalDouyinOverConfiguredPath()
    {
        var root = CreateTempRoot();
        try
        {
            var local = Path.Combine(root, "抖音直播弹幕助手.exe");
            File.WriteAllBytes(local, new byte[] { 0 });
            var configured = Path.Combine(root, "elsewhere", "douyin-danmaku.exe");

            var resolved = SidecarLocator.ResolveDouyin(configured, root);
            Assert.Equal(Path.GetFullPath(local), resolved);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void SameDirectory_FindsVersionedKugouExe()
    {
        var root = CreateTempRoot();
        try
        {
            var local = Path.Combine(root, "酷狗api_v1.5.exe");
            File.WriteAllBytes(local, new byte[] { 0 });

            var resolved = SidecarLocator.ResolveKugou(@"D:\missing\酷狗api.exe", root);
            Assert.Equal(Path.GetFullPath(local), resolved);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void GetMissingRequiredFiles_ReportsMissingExeAndKgapijs()
    {
        var root = CreateTempRoot();
        try
        {
            var missing = SidecarLocator.GetMissingRequiredFiles(
                Path.Combine(root, "missing-douyin.exe"),
                Path.Combine(root, "missing-kugou.exe"));
            Assert.Contains(SidecarLocator.PreferredDouyinFileName, missing);
            Assert.Contains(SidecarLocator.PreferredKugouFileName, missing);

            var kugouExe = Path.Combine(root, SidecarLocator.PreferredKugouFileName);
            File.WriteAllBytes(kugouExe, new byte[] { 0 });
            var onlyKgapijsMissing = SidecarLocator.GetMissingRequiredFiles(
                Path.Combine(root, SidecarLocator.PreferredDouyinFileName),
                kugouExe);
            Assert.Contains($"{SidecarLocator.KugouJsFolderName}\\", onlyKgapijsMissing);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void GetMissingRequiredFiles_ReturnsEmptyWhenPresent()
    {
        var root = CreateTempRoot();
        try
        {
            File.WriteAllBytes(Path.Combine(root, SidecarLocator.PreferredDouyinFileName), new byte[] { 0 });
            File.WriteAllBytes(Path.Combine(root, SidecarLocator.PreferredKugouFileName), new byte[] { 0 });
            Directory.CreateDirectory(Path.Combine(root, SidecarLocator.KugouJsFolderName));
            File.WriteAllText(
                Path.Combine(root, SidecarLocator.KugouJsFolderName, SidecarLocator.KgapiJsEntryFileName),
                "// test");

            var missing = SidecarLocator.GetMissingRequiredFiles(
                Path.Combine(root, SidecarLocator.PreferredDouyinFileName),
                Path.Combine(root, SidecarLocator.PreferredKugouFileName));
            Assert.Empty(missing);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Bootstrap_CopiesKugouFromRepoSidecarsFolder()
    {
        var repo = CreateTempRoot();
        var publish = Path.Combine(repo, "publish", "app");
        var donor = Path.Combine(repo, "sidecars");
        Directory.CreateDirectory(publish);
        Directory.CreateDirectory(donor);
        File.WriteAllBytes(Path.Combine(donor, SidecarLocator.PreferredDouyinFileName), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(donor, SidecarLocator.PreferredKugouFileName), new byte[] { 2 });
        Directory.CreateDirectory(Path.Combine(donor, SidecarLocator.KugouJsFolderName));
        File.WriteAllText(
            Path.Combine(donor, SidecarLocator.KugouJsFolderName, SidecarLocator.KgapiJsEntryFileName),
            "console.log('ok');");

        Environment.SetEnvironmentVariable("LA_TEST_EXE_DIR", publish);
        try
        {
            SidecarBootstrap.EnsureReady();

            var douyin = SidecarLocator.ResolveDouyin("", publish);
            var kugou = SidecarLocator.ResolveKugou("", publish);
            var missing = SidecarLocator.GetMissingRequiredFiles(douyin, kugou);
            Assert.Empty(missing);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LA_TEST_EXE_DIR", null);
            Directory.Delete(repo, true);
        }
    }

    [Fact]
    public void Bootstrap_RepairsEmptyKgapiJsDirectory()
    {
        var repo = CreateTempRoot();
        var publish = Path.Combine(repo, "publish", "app");
        var donor = Path.Combine(repo, "sidecars");
        Directory.CreateDirectory(publish);
        Directory.CreateDirectory(Path.Combine(publish, SidecarLocator.KugouJsFolderName));
        Directory.CreateDirectory(donor);
        File.WriteAllBytes(Path.Combine(donor, SidecarLocator.PreferredKugouFileName), new byte[] { 2 });
        Directory.CreateDirectory(Path.Combine(donor, SidecarLocator.KugouJsFolderName));
        File.WriteAllText(
            Path.Combine(donor, SidecarLocator.KugouJsFolderName, SidecarLocator.KgapiJsEntryFileName),
            "console.log('ok');");

        Environment.SetEnvironmentVariable("LA_TEST_EXE_DIR", publish);
        try
        {
            SidecarBootstrap.EnsureReady();
            Assert.True(SidecarLocator.IsKgapiJsReady(Path.Combine(publish, SidecarLocator.KugouJsFolderName)));
        }
        finally
        {
            Environment.SetEnvironmentVariable("LA_TEST_EXE_DIR", null);
            Directory.Delete(repo, true);
        }
    }

    [Fact]
    public void GetMissingRequiredFiles_ReportsEmptyKgapiJsDirectory()
    {
        var root = CreateTempRoot();
        try
        {
            File.WriteAllBytes(Path.Combine(root, SidecarLocator.PreferredDouyinFileName), new byte[] { 0 });
            File.WriteAllBytes(Path.Combine(root, SidecarLocator.PreferredKugouFileName), new byte[] { 0 });
            Directory.CreateDirectory(Path.Combine(root, SidecarLocator.KugouJsFolderName));

            var missing = SidecarLocator.GetMissingRequiredFiles(
                Path.Combine(root, SidecarLocator.PreferredDouyinFileName),
                Path.Combine(root, SidecarLocator.PreferredKugouFileName));
            Assert.Contains($"{SidecarLocator.KugouJsFolderName}\\", missing);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void IgnoresFudaiAndUnmannedLiveExe()
    {
        Assert.True(SidecarLocator.LooksLikeFudaiOrUnrelated("福袋助手.exe"));
        Assert.True(SidecarLocator.LooksLikeFudaiOrUnrelated("抖音24小时无人直播.exe"));
        Assert.False(SidecarLocator.IsDouyinApiExe("福袋助手.exe"));
        Assert.True(SidecarLocator.IsDouyinApiExe("douyin-danmaku.exe"));
        Assert.True(SidecarLocator.IsDouyinApiExe("抖音直播弹幕助手.exe"));
        Assert.Equal("", SidecarLocator.DouyinStartArgs(@"E:\x\抖音直播弹幕助手.exe"));
        Assert.Equal("-api", SidecarLocator.DouyinStartArgs(@"E:\x\douyin-danmaku.exe"));
        Assert.True(SidecarLocator.IsKugouApiExe("酷狗api_v1.9.exe"));
    }

    private static string CreateTempRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "la_sidecar_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
