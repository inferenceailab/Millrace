using System.Text.RegularExpressions;

namespace Weft.Storage.PostgreSql;

/// <summary>Options for the PostgreSQL provider.</summary>
public sealed partial class PostgreSqlStorageOptions
{
    private string _schema = "weft";

    /// <summary>
    /// Schema holding the Weft tables (default <c>weft</c>). Restricted to
    /// <c>[a-z_][a-z0-9_]*</c> because it is interpolated into DDL/DML.
    /// </summary>
    public string Schema
    {
        get => _schema;
        set
        {
            if (!SchemaName().IsMatch(value))
            {
                throw new ArgumentException(
                    $"Schema '{value}' must match [a-z_][a-z0-9_]* .", nameof(value));
            }

            _schema = value;
        }
    }

    /// <summary>
    /// Creates the schema and tables on first use (idempotent DDL). Disable when migrations
    /// are managed at deploy time; call <c>PostgreSqlStorage.InitializeAsync</c> instead.
    /// </summary>
    public bool AutoCreateSchema { get; set; } = true;

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$")]
    private static partial Regex SchemaName();
}
