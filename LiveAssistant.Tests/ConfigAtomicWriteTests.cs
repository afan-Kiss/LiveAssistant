using System.Text.Json;
using LiveAssistant.Utils;
using Xunit;

namespace LiveAssistant.Tests;

public sealed class ConfigAtomicWriteTests
{
    [Fact]
    public void AtomicWrite_PreservesBackupOnReplace()
    {
        var dir = CreateTempDir();
        var path = Path.Combine(dir, "appsettings.json");
        AtomicFileWriter.WriteAllText(path, """{"version":1}""");
        AtomicFileWriter.WriteAllText(path, """{"version":2}""");

        Assert.Equal("""{"version":2}""", File.ReadAllText(path).Trim());
        Assert.True(File.Exists(path + ".bak"));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void RecoverCorruptFile_RestoresFromBackup()
    {
        var dir = CreateTempDir();
        var path = Path.Combine(dir, "appsettings.json");
        AtomicFileWriter.WriteAllText(path, """{"ok":true}""");
        AtomicFileWriter.WriteAllText(path, """{"ok":true,"v":2}"""); // creates .bak of first version
        File.WriteAllBytes(path, new byte[128]); // simulate 0x00 corruption

        var recovered = AtomicFileWriter.RecoverCorruptFile(path);
        Assert.False(string.IsNullOrWhiteSpace(recovered));
        Assert.Contains("ok", recovered!, StringComparison.Ordinal);
        Assert.True(Directory.GetFiles(dir, "appsettings.json.bad.*").Length >= 1);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void InvalidJson_IsNotTreatedAsEmptyCorruption()
    {
        var dir = CreateTempDir();
        var path = Path.Combine(dir, "appsettings.json");
        File.WriteAllText(path, "{not-json");

        Assert.False(AtomicFileWriter.IsCorruptOrEmpty(path));
        var recovered = AtomicFileWriter.RecoverCorruptFile(path);
        Assert.Equal("{not-json", recovered);
        Assert.Equal("{not-json", File.ReadAllText(path));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void EmptyFile_IsDetectedAsCorrupt()
    {
        var dir = CreateTempDir();
        var path = Path.Combine(dir, "appsettings.json");
        File.WriteAllText(path, "");

        Assert.True(AtomicFileWriter.IsCorruptOrEmpty(path));
        Directory.Delete(dir, true);
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "la-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
