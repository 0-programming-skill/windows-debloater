using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Debloater.Core.Catalog;

/// <summary>Configuração única de (de)serialização YAML do catálogo.</summary>
public static class CatalogYaml
{
    /// <summary>Deserializador estrito: campos desconhecidos são erro (pega erros de digitação).</summary>
    public static IDeserializer Deserializer { get; } = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    public static ISerializer Serializer { get; } = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        // Não usar OmitEmptyCollections: string é IEnumerable e "" seria omitida.
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        // Aspas onde necessário: "" (valor padrão de chave) e "0" (string que parece número) sobrevivem ao round-trip.
        .WithQuotingNecessaryStrings()
        .Build();
}
