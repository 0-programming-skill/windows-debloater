using System.ComponentModel;
using System.Globalization;
using Microsoft.Win32;

namespace Debloater.Core.Platform.FeatureFlags;

/// <summary>Access to Windows feature configurations (API + registry overrides). Replaceable in tests.</summary>
public interface IFeatureStore
{
    /// <summary>Winning configuration of a feature in the store, or null when nothing configures it (built-in default).</summary>
    FeatureConfiguration? Query(uint featureId, FeatureConfigurationType type = FeatureConfigurationType.Runtime);

    /// <summary>Every configuration layer in the store (an id may appear once per priority).</summary>
    IReadOnlyList<FeatureConfiguration> QueryAll(FeatureConfigurationType type = FeatureConfigurationType.Runtime);

    /// <summary>Changes the runtime store (requires administrator; lost on reboot).</summary>
    void SetRuntime(FeatureConfigurationUpdate update);

    /// <summary>Persistent override at a priority, or null when the registry has none.</summary>
    FeatureOverride? ReadOverride(FeaturePriority priority, uint featureId);

    /// <summary>Replaces the override key with exactly <see cref="FeatureOverride.Values"/> (requires administrator).</summary>
    void WriteOverride(FeatureOverride value);

    /// <summary>Deletes the override key (no error when it does not exist).</summary>
    void DeleteOverride(FeaturePriority priority, uint featureId);
}

/// <summary>Real store: ntdll feature configuration API and HKLM\SYSTEM\...\FeatureManagement\Overrides.</summary>
public sealed class NativeFeatureStore : IFeatureStore
{
    public const string OverridesKey = @"SYSTEM\CurrentControlSet\Control\FeatureManagement\Overrides";

    public static NativeFeatureStore Instance { get; } = new();

    public FeatureConfiguration? Query(uint featureId, FeatureConfigurationType type = FeatureConfigurationType.Runtime)
    {
        ulong stamp = 0;
        int status = FeatureConfigurationNative.RtlQueryFeatureConfiguration(featureId, (int)type, ref stamp, out var c);
        if (status == FeatureConfigurationNative.StatusNotFound) return null;
        ThrowIfFailed(status, $"query feature {featureId}");
        return FeatureConfiguration.From(c);
    }

    public unsafe IReadOnlyList<FeatureConfiguration> QueryAll(FeatureConfigurationType type = FeatureConfigurationType.Runtime)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            ulong stamp = 0;
            uint count = 0;
            int status = FeatureConfigurationNative.RtlQueryAllFeatureConfigurations((int)type, ref stamp, null, ref count);
            if (status == FeatureConfigurationNative.StatusSuccess && count == 0) return [];
            if (status is not (FeatureConfigurationNative.StatusBufferOverflow or FeatureConfigurationNative.StatusBufferTooSmall or FeatureConfigurationNative.StatusSuccess))
                ThrowIfFailed(status, "query all feature configurations");

            // Some headroom: the store may grow between the two calls.
            var buffer = new FeatureConfigurationNative.RtlFeatureConfiguration[count + 64];
            uint size = (uint)buffer.Length;
            fixed (FeatureConfigurationNative.RtlFeatureConfiguration* p = buffer)
                status = FeatureConfigurationNative.RtlQueryAllFeatureConfigurations((int)type, ref stamp, p, ref size);
            if (status is FeatureConfigurationNative.StatusBufferOverflow or FeatureConfigurationNative.StatusBufferTooSmall) continue;
            ThrowIfFailed(status, "query all feature configurations");
            var result = new List<FeatureConfiguration>((int)size);
            for (int i = 0; i < size; i++) result.Add(FeatureConfiguration.From(buffer[i]));
            return result;
        }
        throw new InvalidOperationException("The feature configuration store kept changing while it was being read.");
    }

    public unsafe void SetRuntime(FeatureConfigurationUpdate update)
    {
        var u = new FeatureConfigurationNative.RtlFeatureConfigurationUpdate
        {
            FeatureId = update.FeatureId,
            Priority = (int)update.Priority,
            EnabledState = update.Reset ? 0 : (int)update.State,
            Operation = update.Reset ? FeatureConfigurationNative.OperationResetState : FeatureConfigurationNative.OperationFeatureState,
        };
        // NULL previous change stamp: no optimistic concurrency check (a stale stamp fails with STATUS_UNSUCCESSFUL).
        int status = FeatureConfigurationNative.RtlSetFeatureConfigurations(null, (int)FeatureConfigurationType.Runtime, &u, 1);
        ThrowIfFailed(status, $"set feature {update.FeatureId} ({FeatureFlagText.Priority(update.Priority)})");
    }

    private static string SubKey(FeaturePriority priority, uint featureId) =>
        $@"{OverridesKey}\{((int)priority).ToString(CultureInfo.InvariantCulture)}\{FeatureOverrideKey.Encode(featureId).ToString(CultureInfo.InvariantCulture)}";

    private static RegistryKey Hklm() => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

    public FeatureOverride? ReadOverride(FeaturePriority priority, uint featureId)
    {
        using var hklm = Hklm();
        using var key = hklm.OpenSubKey(SubKey(priority, featureId), writable: false);
        return key is null ? null : ReadKey(key, priority, featureId);
    }

    private static FeatureOverride ReadKey(RegistryKey key, FeaturePriority priority, uint featureId)
    {
        var values = new SortedDictionary<string, uint>(StringComparer.Ordinal);
        foreach (var name in key.GetValueNames())
        {
            if (key.GetValueKind(name) != RegistryValueKind.DWord)
                throw new InvalidOperationException($"unexpected non-DWORD value '{name}' in {key.Name}; not touching it.");
            values[name] = unchecked((uint)(int)key.GetValue(name, 0)!);
        }
        return new FeatureOverride(featureId, priority, values);
    }

    public void WriteOverride(FeatureOverride value)
    {
        using var hklm = Hklm();
        var path = SubKey(value.Priority, value.FeatureId);
        using var key = hklm.CreateSubKey(path, writable: true)
            ?? throw new InvalidOperationException($@"could not create HKLM\{path}.");
        foreach (var name in key.GetValueNames())
            if (!value.Values.ContainsKey(name)) key.DeleteValue(name, throwOnMissingValue: false);
        foreach (var (name, data) in value.Values)
            key.SetValue(name, unchecked((int)data), RegistryValueKind.DWord);
    }

    public void DeleteOverride(FeaturePriority priority, uint featureId)
    {
        using var hklm = Hklm();
        hklm.DeleteSubKeyTree(SubKey(priority, featureId), throwOnMissingSubKey: false);
    }

    private static void ThrowIfFailed(int status, string what)
    {
        if (status >= 0) return;
        var win32 = (int)FeatureConfigurationNative.RtlNtStatusToDosError(status);
        throw new InvalidOperationException($"{what} failed: NTSTATUS 0x{status:X8} ({new Win32Exception(win32).Message})");
    }
}
