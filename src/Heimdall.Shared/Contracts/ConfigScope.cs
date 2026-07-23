namespace Heimdall.Shared.Contracts;

public enum ConfigScope
{
    All = 0,
    /// <summary>Legacy MachineGroup string match.</summary>
    Group = 1,
    Machine = 2,
    Region = 3,
    Office = 4,
    /// <summary>Country derived from region / machine (e.g. Australia).</summary>
    Country = 5
}

/// <summary>Which process list a pause applies to.</summary>
public enum ProcessListKind
{
    /// <summary>Paused include = do not track until expiry.</summary>
    Include = 0,
    /// <summary>Paused exclude = do not apply this noise filter until expiry.</summary>
    Exclude = 1
}
