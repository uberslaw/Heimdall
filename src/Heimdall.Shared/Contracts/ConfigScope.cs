namespace Heimdall.Shared.Contracts;

public enum ConfigScope
{
    All = 0,
    /// <summary>Legacy MachineGroup string match.</summary>
    Group = 1,
    Machine = 2,
    Region = 3,
    Office = 4
}
