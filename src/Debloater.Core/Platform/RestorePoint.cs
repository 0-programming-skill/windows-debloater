using System.Management;

namespace Debloater.Core.Platform;

/// <summary>Resultado da tentativa de criar um ponto de restauração.</summary>
/// <param name="Created">true se um ponto novo foi criado.</param>
/// <param name="Message">Explicação em pt-BR (inclui o motivo quando nada foi criado).</param>
public sealed record RestorePointResult(bool Created, string Message);

/// <summary>
/// Cria pontos de restauração do sistema via WMI (root\default:SystemRestore.CreateRestorePoint).
/// Requer administrador e a Proteção do Sistema ativa na unidade do Windows. O Windows cria no
/// máximo um ponto a cada 24 h (SystemRestorePointCreationFrequency): dentro desse intervalo o método
/// retorna sucesso sem criar nada. O programa NÃO altera essa configuração; apenas avisa.
/// </summary>
public static class RestorePoint
{
    private const string Scope = @"\\.\root\default";

    // RestorePointType 12 = MODIFY_SETTINGS; EventType 100 = BEGIN_SYSTEM_CHANGE.
    private const int ModifySettings = 12;
    private const int BeginSystemChange = 100;

    /// <summary>Data do ponto de restauração mais recente (UTC), ou null se não houver / não der para ler.</summary>
    public static DateTime? LatestUtc()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(Scope, "SELECT CreationTime FROM SystemRestore");
            DateTime? latest = null;
            foreach (var obj in searcher.Get())
            {
                using (obj)
                {
                    if (obj["CreationTime"] is string s)
                    {
                        var dt = ManagementDateTimeConverter.ToDateTime(s).ToUniversalTime();
                        if (latest is null || dt > latest) latest = dt;
                    }
                }
            }
            return latest;
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    public static Task<RestorePointResult> CreateAsync(string description, CancellationToken ct = default) =>
        Task.Run(() => Create(description), ct);

    public static RestorePointResult Create(string description)
    {
        if (!SystemInfo.IsProcessElevated())
            return new RestorePointResult(false, "Criar ponto de restauração requer administrador.");

        var before = LatestUtc();
        uint code;
        try
        {
            using var cls = new ManagementClass(Scope, "SystemRestore", null);
            using var input = cls.GetMethodParameters("CreateRestorePoint");
            input["Description"] = description;
            input["RestorePointType"] = ModifySettings;
            input["EventType"] = BeginSystemChange;
            using var output = cls.InvokeMethod("CreateRestorePoint", input, null);
            code = Convert.ToUInt32(output?["ReturnValue"] ?? 0u);
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return new RestorePointResult(false, $"Falha ao criar o ponto de restauração: {ex.Message}");
        }

        if (code != 0)
        {
            var hint = code == 1058
                ? " A Proteção do Sistema parece estar desativada (ative em Propriedades do Sistema > Proteção do Sistema)."
                : "";
            return new RestorePointResult(false, $"O Windows recusou o ponto de restauração (código {code}).{hint}");
        }

        var after = LatestUtc();
        if (after is not null && (before is null || after > before))
            return new RestorePointResult(true, $"Ponto de restauração criado ({after.Value.ToLocalTime():g}).");

        // Sucesso sem ponto novo: limite de frequência do Windows (um a cada 24 h).
        var when = before is null ? "" : $" O mais recente é de {before.Value.ToLocalTime():g}.";
        return new RestorePointResult(false,
            "O Windows não criou um ponto novo: ele permite no máximo um a cada 24 horas." + when +
            " Esse ponto existente continua disponível para restauração.");
    }
}
