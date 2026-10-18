namespace Debloater.Core.Updates;

/// <summary>
/// Version of the catalog format this engine understands. A published catalog bundle declares the schema it
/// was packed with; the updater only installs bundles with a schema in
/// [<see cref="MinSupportedVersion"/>, <see cref="CurrentVersion"/>].
/// </summary>
/// <remarks>
/// Bump rules:
/// <list type="bullet">
/// <item>Bump <see cref="CurrentVersion"/> whenever the catalog starts using something an older engine would
/// reject or misread: a new action type, a new required field, a changed meaning of an existing field, a new
/// top-level file layout. Older apps then keep their current catalog instead of installing one they cannot
/// load.</item>
/// <item>Purely additive optional fields that older engines ignore safely do not need a bump.</item>
/// <item>Raise <see cref="MinSupportedVersion"/> only when this engine can no longer load old catalogs.</item>
/// </list>
/// </remarks>
public static class CatalogSchema
{
    public const int CurrentVersion = 1;

    public const int MinSupportedVersion = 1;

    /// <summary>Engine version compared with a bundle's <c>minAppVersion</c> (the Debloater.Core assembly version).</summary>
    public static Version EngineVersion { get; } = typeof(CatalogSchema).Assembly.GetName().Version ?? new Version(0, 0);

    /// <summary>
    /// Why a bundle cannot be used by this engine (null = compatible). <paramref name="engineVersion"/> and the
    /// schema bounds are parameters for tests.
    /// </summary>
    public static string? CheckCompatibility(int schemaVersion, string? minAppVersion, Version? engineVersion = null,
        int minSchema = MinSupportedVersion, int maxSchema = CurrentVersion)
    {
        if (schemaVersion > maxSchema)
            return $"catalog schema {schemaVersion} is newer than this program supports ({maxSchema}); update the program.";
        if (schemaVersion < minSchema)
            return $"catalog schema {schemaVersion} is older than this program supports (minimum {minSchema}).";
        if (!string.IsNullOrWhiteSpace(minAppVersion))
        {
            if (!Version.TryParse(minAppVersion, out var min))
                return $"invalid minAppVersion '{minAppVersion}'.";
            var engine = engineVersion ?? EngineVersion;
            if (Normalize(engine) < Normalize(min))
                return $"the catalog requires program version {minAppVersion} or later (this is {engine}).";
        }
        return null;
    }

    /// <summary>Missing components count as 0 (1.2 == 1.2.0.0).</summary>
    private static Version Normalize(Version v) =>
        new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
}
