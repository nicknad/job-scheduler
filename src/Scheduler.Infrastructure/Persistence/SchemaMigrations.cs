namespace Scheduler.Infrastructure.Persistence;

/// <summary>
/// The ordered set of registry schema migrations. Version 1 is the initial
/// schema from <see cref="SchemaDefinitions" />; new versions append.
/// </summary>
public static class SchemaMigrations
{
    public static IReadOnlyList<Migration> All { get; } =
    [
        new Migration(
            Version: 1,
            Name: "initial-registry-schema",
            Sql: string.Join(Environment.NewLine, SchemaDefinitions.All)),
    ];

    public static int LatestVersion => All[^1].Version;
}
