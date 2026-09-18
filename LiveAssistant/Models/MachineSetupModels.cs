using System.Text.Json.Serialization;

namespace LiveAssistant.Models;

public sealed class MachineSetupStatus
{
    public string KitRoot { get; set; } = "";
    public bool KitReady { get; set; }
    public string Summary { get; set; } = "";
    public List<MachineSetupComponentStatus> Components { get; set; } = new();
    public List<string> PackHints { get; set; } = new();
}

public sealed class MachineSetupComponentStatus
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public string State { get; set; } = "missing"; // ok | missing | broken | skipped | needs_reboot
    public bool CanInstall { get; set; }
    public string Detail { get; set; } = "";
    public string FixHint { get; set; } = "";
    public string? DetectedPath { get; set; }
}

public sealed class MachineSetupInstallRequest
{
    /// <summary>空或 all = 全部可安装项；否则按组件 id。</summary>
    public string? ComponentId { get; set; }
}

public sealed class MachineSetupInstallResult
{
    public bool Ok { get; set; }
    public string Summary { get; set; } = "";
    public List<MachineSetupStepResult> Steps { get; set; } = new();
    public MachineSetupStatus? StatusAfter { get; set; }
}

public sealed class MachineSetupStepResult
{
    public string ComponentId { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Success { get; set; }
    public bool Skipped { get; set; }
    public string Message { get; set; } = "";
    /// <summary>失败时的具体原因（面向换机排障）。</summary>
    public string? FailureReason { get; set; }
    public string? LogTail { get; set; }
}

public sealed class MachineSetupManifest
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("components")]
    public List<MachineSetupManifestItem> Components { get; set; } = new();
}

public sealed class MachineSetupManifestItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("relativePath")]
    public string RelativePath { get; set; } = "";

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = ""; // copy_file | copy_dir | installer_exe | note

    [JsonPropertyName("targetRelativePath")]
    public string? TargetRelativePath { get; set; }

    [JsonPropertyName("installArgs")]
    public string? InstallArgs { get; set; }

    [JsonPropertyName("required")]
    public bool Required { get; set; } = true;
}
