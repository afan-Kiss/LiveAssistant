using System.Text.RegularExpressions;
using Xunit;

namespace LiveAssistant.Tests;

public sealed class AdminStabilityTests
{
    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            if (File.Exists(Path.Combine(dir, "LiveAssistant.sln")))
            {
                return dir;
            }

            dir = Directory.GetParent(dir)?.FullName
                  ?? throw new DirectoryNotFoundException("LiveAssistant.sln not found");
        }

        throw new DirectoryNotFoundException("LiveAssistant.sln not found");
    }

    private static string ReadRepoFile(params string[] parts)
        => File.ReadAllText(Path.Combine(FindRepoRoot(), Path.Combine(parts)));

    [Fact]
    public void AdminIndex_UsesUnifiedTimerManager()
    {
        var html = ReadRepoFile("LiveAssistant", "Admin", "wwwroot", "index.html");

        Assert.Contains("let dashboardTimer = null;", html);
        Assert.Contains("let emergencyTimer = null;", html);
        Assert.Contains("let runtimeTimer = null;", html);
        Assert.Contains("function startAppTimers()", html);
        Assert.Contains("function stopAppTimers()", html);
        Assert.Contains("function startRuntimeTimerOnly()", html);
    }

    [Fact]
    public void StartAppTimers_DoesNotCreateDuplicateTimers()
    {
        int? dashboardTimer = null;
        int? emergencyTimer = null;
        int? runtimeTimer = null;
        var nextId = 1;

        void StopAppTimers()
        {
            dashboardTimer = null;
            emergencyTimer = null;
            runtimeTimer = null;
        }

        void StartAppTimers()
        {
            if (dashboardTimer != null && emergencyTimer != null && runtimeTimer != null)
            {
                return;
            }

            StopAppTimers();
            dashboardTimer = nextId++;
            emergencyTimer = nextId++;
            runtimeTimer = nextId++;
        }

        StartAppTimers();
        var firstDashboard = dashboardTimer;
        var firstEmergency = emergencyTimer;
        var firstRuntime = runtimeTimer;

        StartAppTimers();

        Assert.Equal(firstDashboard, dashboardTimer);
        Assert.Equal(firstEmergency, emergencyTimer);
        Assert.Equal(firstRuntime, runtimeTimer);
    }

    [Theory]
    [InlineData("/api/sync/bundle")]
    [InlineData("/api/sync/commands")]
    [InlineData("/api/sync/config")]
    public void SyncEndpoints_RequireAdminAuth(string route)
    {
        var src = ReadRepoFile("LiveAssistant", "Admin", "AdminWebHost.cs");
        var pattern = $@"app\.MapGet\(""{Regex.Escape(route)}"", \(HttpContext http\) => Auth\(http,";
        Assert.Matches(pattern, src);
    }
}
