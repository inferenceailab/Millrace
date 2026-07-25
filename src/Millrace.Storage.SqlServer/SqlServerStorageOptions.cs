namespace Millrace.Storage.SqlServer;

/// <summary>Configuration for the SQL Server provider.</summary>
public sealed class SqlServerStorageOptions
{
    /// <summary>Schema the tables live in. Created on first use unless disabled.</summary>
    public string Schema { get; set; } = "millrace";

    /// <summary>Creates the schema and tables lazily on first use.</summary>
    public bool AutoCreateSchema { get; set; } = true;
}
