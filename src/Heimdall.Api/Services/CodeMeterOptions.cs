namespace Heimdall.Api.Services;

/// <summary>TUFLOW CodeMeter network license poll (HPC / Classic) for Flood Live.</summary>
public sealed class CodeMeterOptions
{
    public const string SectionName = "Heimdall:CodeMeter";

    /// <summary>When false, poller does not run and Flood Live hides/marks licenses unavailable.</summary>
    public bool Enabled { get; set; }

    public string Cmu32Path { get; set; } =
        @"C:\Program Files\CodeMeter\Runtime\bin\cmu32.exe";

    /// <summary>Seconds between poll starts. Skips a tick if the previous poll is still running.</summary>
    public int PollSeconds { get; set; } = 60;

    public int InitialDelaySeconds { get; set; } = 5;

    /// <summary>Per cmu32 invocation timeout.</summary>
    public int QueryTimeoutSeconds { get; set; } = 90;

    public CodeMeterProductOptions Hpc { get; set; } = new()
    {
        ProductCode = 926,
        TotalLicenses = 32
    };

    public CodeMeterProductOptions Classic { get; set; } = new()
    {
        ProductCode = 920,
        TotalLicenses = 32
    };
}

public sealed class CodeMeterProductOptions
{
    public int ProductCode { get; set; }
    public int TotalLicenses { get; set; } = 32;
    public List<CodeMeterServerEntry> Servers { get; set; } = [];
}

public sealed class CodeMeterServerEntry
{
    public string Fqdn { get; set; } = "";
    public string Serial { get; set; } = "";
}
