using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Debloater.Core.Catalog;

/// <summary>Single YAML (de)serialization configuration of the catalog.</summary>
public static class CatalogYaml
{
    /// <summary>Strict deserializer: unknown fields are errors (catches typos).</summary>
    public static IDeserializer Deserializer { get; } = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    public static ISerializer Serializer { get; } = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        // Do not use OmitEmptyCollections: string is IEnumerable and "" would be omitted.
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        // Quotes where needed: "" (key default value) and "0" (string that looks like a number) survive the round-trip.
        .WithQuotingNecessaryStrings()
        .Build();
}
