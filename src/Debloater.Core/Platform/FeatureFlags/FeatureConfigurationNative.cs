using System.Runtime.InteropServices;

namespace Debloater.Core.Platform.FeatureFlags;

/// <summary>
/// P/Invoke declarations for the ntdll feature configuration API ("feature flags", the WIL/velocity
/// switches that gate new Windows code). These exports are public but undocumented; the layouts below were
/// checked on 26200 and 26300 (see the unit tests for the sizes and bit positions).
///
/// Notes from testing on 26300.9711:
/// - Querying works without elevation (the store is a section mapped into every process).
/// - <c>RtlSetFeatureConfigurations</c> only accepts the runtime store (the boot store returns
///   STATUS_INVALID_PARAMETER); it needs administrator rights and the change is lost on reboot.
/// - A stale change stamp returns STATUS_UNSUCCESSFUL; passing NULL skips the check.
/// - <c>RtlQueryAllFeatureConfigurations</c> returns every priority layer (the same id may appear several
///   times), sorted by id; <c>RtlQueryFeatureConfiguration</c> returns only the winning (highest priority) one.
/// </summary>
internal static partial class FeatureConfigurationNative
{
    public const int StatusSuccess = 0;
    public const int StatusBufferOverflow = unchecked((int)0x80000005);
    public const int StatusBufferTooSmall = unchecked((int)0xC0000023);
    public const int StatusNotFound = unchecked((int)0xC0000225);

    /// <summary>RTL_FEATURE_CONFIGURATION (12 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RtlFeatureConfiguration
    {
        public uint FeatureId;

        /// <summary>
        /// Bit field: Priority (bits 0-3), EnabledState (4-5), IsWexpConfiguration (6), HasSubscriptions (7),
        /// Variant (8-13), VariantPayloadKind (14-15), reserved (16-31).
        /// </summary>
        public uint Flags;

        public uint VariantPayload;
    }

    /// <summary>RTL_FEATURE_CONFIGURATION_UPDATE (32 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RtlFeatureConfigurationUpdate
    {
        public uint FeatureId;
        public int Priority;
        public int EnabledState;
        public int EnabledStateOptions;
        /// <summary>UCHAR Variant followed by 3 reserved bytes.</summary>
        public uint Variant;
        public int VariantPayloadKind;
        public uint VariantPayload;
        public int Operation;
    }

    /// <summary>RTL_FEATURE_CONFIGURATION_OPERATION (flags).</summary>
    public const int OperationFeatureState = 1;
    public const int OperationVariantState = 2;
    public const int OperationResetState = 4;

    [LibraryImport("ntdll.dll")]
    public static partial int RtlQueryFeatureConfiguration(uint featureId, int featureType, ref ulong changeStamp, out RtlFeatureConfiguration configuration);

    [LibraryImport("ntdll.dll")]
    public static unsafe partial int RtlQueryAllFeatureConfigurations(int featureType, ref ulong changeStamp, RtlFeatureConfiguration* configurations, ref uint count);

    [LibraryImport("ntdll.dll")]
    public static partial ulong RtlQueryFeatureConfigurationChangeStamp();

    [LibraryImport("ntdll.dll")]
    public static unsafe partial int RtlSetFeatureConfigurations(ulong* previousChangeStamp, int featureType, RtlFeatureConfigurationUpdate* updates, uint count);

    [LibraryImport("ntdll.dll")]
    public static partial uint RtlNtStatusToDosError(int status);

    // ---------------- bit field helpers ----------------

    public static FeaturePriority Priority(uint flags) => (FeaturePriority)(flags & 0xF);
    public static FeatureEnabledState EnabledState(uint flags) => (FeatureEnabledState)((flags >> 4) & 0x3);
    public static bool IsWexpConfiguration(uint flags) => (flags & 0x40) != 0;
    public static bool HasSubscriptions(uint flags) => (flags & 0x80) != 0;
    public static int Variant(uint flags) => (int)((flags >> 8) & 0x3F);
    public static int VariantPayloadKind(uint flags) => (int)((flags >> 14) & 0x3);

    public static uint PackFlags(FeaturePriority priority, FeatureEnabledState state, bool wexp = false, bool subscriptions = false, int variant = 0, int payloadKind = 0) =>
        ((uint)priority & 0xF) | (((uint)state & 0x3) << 4) | (wexp ? 0x40u : 0) | (subscriptions ? 0x80u : 0)
        | (((uint)variant & 0x3F) << 8) | (((uint)payloadKind & 0x3) << 14);
}
