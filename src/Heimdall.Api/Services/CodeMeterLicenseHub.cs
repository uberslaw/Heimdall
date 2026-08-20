namespace Heimdall.Api.Services;

/// <summary>Latest CodeMeter poll snapshot shared by Flood Live broadcast and SSR.</summary>
public sealed class CodeMeterLicenseHub
{
    private readonly object _gate = new();
    private CodeMeterLicenseSnapshot _latest = CodeMeterLicenseSnapshot.Disabled;

    public CodeMeterLicenseSnapshot Latest
    {
        get { lock (_gate) return _latest; }
    }

    public void Publish(CodeMeterLicenseSnapshot snapshot)
    {
        lock (_gate) _latest = snapshot;
    }
}
