using LiveAssistant;
using Xunit;

namespace LiveAssistant.Tests;

public sealed class SingleFileStartupTests
{
    [Fact]
    public void ExeDirectory_Uses_LA_TEST_EXE_DIR()
    {
        var root = CreateTempRoot();
        try
        {
            Environment.SetEnvironmentVariable("LA_TEST_EXE_DIR", root);
            Environment.SetEnvironmentVariable("LA_DATA_DIR", null);
            ResetAppPathsCache();

            Assert.Equal(Path.GetFullPath(root), AppPaths.ExeDirectory);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LA_TEST_EXE_DIR", null);
            ResetAppPathsCache();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ResolveDataDirectory_PrefersExeAdjacentData()
    {
        var root = CreateTempRoot();
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(data);
        try
        {
            Environment.SetEnvironmentVariable("LA_TEST_EXE_DIR", root);
            Environment.SetEnvironmentVariable("LA_DATA_DIR", null);
            ResetAppPathsCache();

            Assert.Equal(Path.GetFullPath(data), AppPaths.ResolveDataDirectory());
        }
        finally
        {
            Environment.SetEnvironmentVariable("LA_TEST_EXE_DIR", null);
            ResetAppPathsCache();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ConfigAndSidecarPaths_AreUnderExeDirectory()
    {
        var root = CreateTempRoot();
        try
        {
            Environment.SetEnvironmentVariable("LA_TEST_EXE_DIR", root);
            ResetAppPathsCache();

            Assert.Equal(Path.Combine(root, "Config"), AppPaths.ConfigDirectory);
            Assert.Equal(root, AppPaths.SidecarDirectory);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LA_TEST_EXE_DIR", null);
            ResetAppPathsCache();
            Directory.Delete(root, true);
        }
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "la-startup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void ResetAppPathsCache()
    {
        var field = typeof(AppPaths).GetField("_cachedExeDirectory",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        field?.SetValue(null, null);
        var dataField = typeof(AppPaths).GetField("_cachedDataDirectory",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        dataField?.SetValue(null, null);
    }
}
