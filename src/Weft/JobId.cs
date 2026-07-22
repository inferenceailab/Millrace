namespace Weft;

/// <summary>Identifies a single job in the substrate. Version 7 GUIDs keep storage indexes append-friendly.</summary>
public readonly record struct JobId(Guid Value)
{
    public static JobId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("n");
}
