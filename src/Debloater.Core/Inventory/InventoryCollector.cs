using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using Debloater.Core.Platform;
using Microsoft.Win32;
using Windows.Management.Deployment;

namespace Debloater.Core.Inventory;

/// <summary>
/// Enumerates (read-only) what exists on the system: services and drivers, scheduled tasks,
/// appx packages, optional features, capabilities and autostart items.
/// Nothing here changes the system.
/// </summary>
public sealed partial class InventoryCollector
{
    private readonly SystemInfo _system;

    public InventoryCollector(SystemInfo? system = null) => _system = system ?? SystemInfo.Current;

    /// <summary>Collects the requested kinds (all, if <paramref name="kinds"/> is null), in parallel.</summary>
    public async Task<InventorySnapshot> CollectAsync(IEnumerable<string>? kinds = null, CancellationToken ct = default)
    {
        var wanted = (kinds ?? InventoryKinds.All).Select(k => k.ToLowerInvariant()).Distinct().ToList();
        var tasks = wanted.Select(k => Task.Run(() => CollectSafe(k), ct)).ToList();
        var sections = await Task.WhenAll(tasks).ConfigureAwait(false);
        return new InventorySnapshot
        {
            CreatedUtc = DateTime.UtcNow,
            WindowsBuild = _system.FullBuild,
            Edition = _system.Edition,
            Elevated = _system.IsElevated,
            Sections = [.. sections.OrderBy(s => InventoryKinds.All.ToList().IndexOf(s.Kind))],
        };
    }

    private InventorySection CollectSafe(string kind)
    {
        try
        {
            var section = kind switch
            {
                InventoryKinds.Services => CollectServices(drivers: false),
                InventoryKinds.Drivers => CollectServices(drivers: true),
                InventoryKinds.Tasks => CollectTasks(),
                InventoryKinds.Appx => CollectAppx(),
                InventoryKinds.Features => CollectFeatures(),
                InventoryKinds.Capabilities => CollectCapabilities(),
                InventoryKinds.Autoruns => CollectAutoruns(),
                _ => throw new ArgumentException($"unknown inventory kind '{kind}' ({string.Join(", ", InventoryKinds.All)})."),
            };
            section.Items.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Id, b.Id));
            return section;
        }
        catch (ArgumentException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new InventorySection { Kind = kind, Available = false, Note = $"failed to enumerate: {ex.Message}" };
        }
    }

    // ---------------- services and drivers ----------------

    // SERVICE_* (winnt.h)
    private const int KernelDriver = 0x1, FileSystemDriver = 0x2, RecognizerDriver = 0x8;
    private const int UserServiceInstance = 0x80;

    private static InventorySection CollectServices(bool drivers)
    {
        var section = new InventorySection { Kind = drivers ? InventoryKinds.Drivers : InventoryKinds.Services };
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var root = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Services")
            ?? throw new InvalidOperationException("Services key not found");
        foreach (var name in root.GetSubKeyNames())
        {
            using var key = root.OpenSubKey(name);
            if (key?.GetValue("Type") is not int type) continue; // not a service (e.g. parameter keys)
            if ((type & UserServiceInstance) != 0) continue;    // per-user instance (name changes at every logon)
            bool isDriver = (type & (KernelDriver | FileSystemDriver | RecognizerDriver)) != 0;
            if (isDriver != drivers) continue;

            var item = new InventoryItem
            {
                Kind = section.Kind,
                Id = name,
                DisplayName = ResolveIndirect(key.GetValue("DisplayName") as string),
            };
            int start = key.GetValue("Start") is int s ? s : -1;
            bool delayed = key.GetValue("DelayedAutostart") is int d && d != 0;
            item.Properties["start"] = StartToString(start, delayed);
            item.Properties["type"] = $"0x{type:X}";
            if (key.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string image)
                item.Properties["imagePath"] = image;
            // DLL of svchost-hosted services (informational: not part of the diff; used by the fingerprint).
            try
            {
                using var parameters = key.OpenSubKey("Parameters");
                if (parameters?.GetValue("ServiceDll", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string dll)
                    (item.Info ??= new(StringComparer.Ordinal))["serviceDll"] = dll;
            }
            catch (System.Security.SecurityException) { }
            section.Items.Add(item);
        }
        return section;
    }

    internal static string StartToString(int start, bool delayed) => start switch
    {
        0 => "boot",
        1 => "system",
        2 => delayed ? "automaticDelayed" : "automatic",
        3 => "manual",
        4 => "disabled",
        _ => $"start={start}",
    };

    [LibraryImport("shlwapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHLoadIndirectString(string pszSource, [Out] char[] pszOutBuf, int cchOutBuf, IntPtr ppvReserved);

    /// <summary>Resolves names in the "@%SystemRoot%\system32\x.dll,-100" format; returns the original on failure.</summary>
    internal static string? ResolveIndirect(string? value)
    {
        if (string.IsNullOrEmpty(value) || value[0] != '@') return value;
        try
        {
            var buffer = new char[1024];
            if (SHLoadIndirectString(value, buffer, buffer.Length, IntPtr.Zero) == 0)
            {
                int len = Array.IndexOf(buffer, '\0');
                return new string(buffer, 0, len < 0 ? buffer.Length : len);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        return value;
    }

    // ---------------- scheduled tasks ----------------

    private const int TaskEnumHidden = 1;

    private static InventorySection CollectTasks()
    {
        var section = new InventorySection { Kind = InventoryKinds.Tasks };
        var type = Type.GetTypeFromProgID("Schedule.Service")
            ?? throw new InvalidOperationException("Task Scheduler (Schedule.Service) unavailable.");
        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();
        int deniedFolders = 0;

        void Walk(dynamic folder)
        {
            try
            {
                foreach (dynamic task in folder.GetTasks(TaskEnumHidden))
                {
                    string path = task.Path;
                    var item = new InventoryItem { Kind = InventoryKinds.Tasks, Id = path, DisplayName = task.Name };
                    item.Properties["enabled"] = ((bool)task.Enabled) ? "true" : "false";
                    section.Items.Add(item);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or COMException) { deniedFolders++; }

            dynamic subfolders;
            try { subfolders = folder.GetFolders(0); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or COMException) { deniedFolders++; return; }
            foreach (dynamic sub in subfolders) Walk(sub);
        }

        Walk(service.GetFolder("\\"));
        if (deniedFolders > 0)
            section.Note = $"{deniedFolders} folder(s) without access (run as administrator to see all).";
        return section;
    }

    // ---------------- appx ----------------

    private InventorySection CollectAppx()
    {
        var section = new InventorySection { Kind = InventoryKinds.Appx };
        var pm = new PackageManager();
        var items = new Dictionary<string, InventoryItem>(StringComparer.OrdinalIgnoreCase);

        InventoryItem Get(string name)
        {
            if (!items.TryGetValue(name, out var item))
                items[name] = item = new InventoryItem { Kind = InventoryKinds.Appx, Id = name, Info = new(StringComparer.Ordinal) };
            return item;
        }

        foreach (var pkg in pm.FindPackagesForUser(""))
        {
            var item = Get(pkg.Id.Name);
            item.Properties["currentUser"] = "true";
            item.Properties["framework"] = pkg.IsFramework ? "true" : "false";
            item.Properties["signature"] = pkg.SignatureKind.ToString();
            item.Properties["publisher"] = pkg.Id.PublisherId;
            item.Info!["version"] = $"{pkg.Id.Version.Major}.{pkg.Id.Version.Minor}.{pkg.Id.Version.Build}.{pkg.Id.Version.Revision}";
            try { item.DisplayName ??= NullIfEmpty(pkg.DisplayName); } catch (COMException) { }
        }

        if (_system.IsElevated)
        {
            try
            {
                foreach (var pkg in pm.FindPackages())
                    Get(pkg.Id.Name).Properties["allUsers"] = "true";
                foreach (var pkg in pm.FindProvisionedPackages())
                    Get(pkg.Id.Name).Properties["provisioned"] = "true";
                foreach (var item in items.Values)
                {
                    item.Properties.TryAdd("allUsers", "false");
                    item.Properties.TryAdd("provisioned", "false");
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or COMException)
            {
                section.Note = $"all-users/provisioned packages unavailable: {ex.Message}";
            }
        }
        else
        {
            section.Note = "current user only; other users' and provisioned packages require administrator.";
        }

        foreach (var item in items.Values) item.Properties.TryAdd("currentUser", "false");
        section.Items.AddRange(items.Values);
        return section;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    // ---------------- optional features ----------------

    private static InventorySection CollectFeatures()
    {
        var section = new InventorySection { Kind = InventoryKinds.Features };
        using var searcher = new ManagementObjectSearcher("root\\CIMV2", "SELECT Name, Caption, InstallState FROM Win32_OptionalFeature");
        using var results = searcher.Get();
        foreach (var mo in results)
        {
            using (mo)
            {
                if (mo["Name"] is not string name) continue;
                uint state = Convert.ToUInt32(mo["InstallState"] ?? 0u, CultureInfo.InvariantCulture);
                var item = new InventoryItem { Kind = InventoryKinds.Features, Id = name, DisplayName = mo["Caption"] as string };
                item.Properties["state"] = state switch { 1 => "enabled", 2 => "disabled", 3 => "absent", _ => $"InstallState={state}" };
                section.Items.Add(item);
            }
        }
        return section;
    }

    // ---------------- capabilities (Features on Demand) ----------------

    private const string CbsKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing";

    /// <summary>
    /// Without administrator there is no API to list capabilities; we use the CBS index in the registry,
    /// which lists the INSTALLED capabilities. As administrator, DISM also lists the available ones.
    /// </summary>
    private InventorySection CollectCapabilities()
    {
        if (_system.IsElevated)
        {
            var fromDism = TryCollectCapabilitiesWithDism();
            if (fromDism is not null) return fromDism;
        }

        var section = new InventorySection
        {
            Kind = InventoryKinds.Capabilities,
            Note = "installed capabilities only (CBS index in the registry); run as administrator to also list the available ones.",
        };
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var index = hklm.OpenSubKey($@"{CbsKey}\CapabilityIndex");
        if (index is null)
        {
            section.Available = false;
            section.Note = "CBS capability index not found; run as administrator.";
            return section;
        }
        foreach (var name in index.GetSubKeyNames())
        {
            var item = new InventoryItem { Kind = InventoryKinds.Capabilities, Id = name };
            item.Properties["state"] = "installed";
            section.Items.Add(item);
        }
        return section;
    }

    private static InventorySection? TryCollectCapabilitiesWithDism()
    {
        var dism = Path.Combine(Environment.SystemDirectory, "dism.exe");
        var result = ProcessRunner.RunAsync(dism, "/Online /Get-Capabilities /Format:Table /English").GetAwaiter().GetResult();
        if (result.ExitCode != 0) return null;
        var section = new InventorySection { Kind = InventoryKinds.Capabilities };
        foreach (var line in result.StdOut.Split('\n'))
        {
            var parts = line.Split('|', StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || !parts[0].Contains("~~")) continue;
            var item = new InventoryItem { Kind = InventoryKinds.Capabilities, Id = parts[0] };
            item.Properties["state"] = parts[1].Equals("Installed", StringComparison.OrdinalIgnoreCase) ? "installed" : parts[1].ToLowerInvariant().Replace(' ', '-');
            section.Items.Add(item);
        }
        return section.Items.Count > 0 ? section : null;
    }

    // ---------------- autostart ----------------

    internal static readonly (RegistryHive Hive, string Prefix, string SubKey)[] RunKeys =
    [
        (RegistryHive.LocalMachine, "HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"),
        (RegistryHive.LocalMachine, "HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce"),
        (RegistryHive.LocalMachine, "HKLM", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run"),
        (RegistryHive.LocalMachine, "HKLM", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce"),
        (RegistryHive.CurrentUser, "HKCU", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"),
        (RegistryHive.CurrentUser, "HKCU", @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce"),
    ];

    private const string StartupApproved = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    private static InventorySection CollectAutoruns()
    {
        var section = new InventorySection { Kind = InventoryKinds.Autoruns };
        foreach (var (hive, prefix, subKey) in RunKeys)
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(subKey);
            if (key is null) continue;
            bool wow = subKey.Contains("WOW6432Node", StringComparison.OrdinalIgnoreCase);
            string approvedKey = $@"{StartupApproved}\{(wow ? "Run32" : "Run")}";
            foreach (var valueName in key.GetValueNames())
            {
                if (valueName.Length == 0) continue;
                var item = new InventoryItem
                {
                    Kind = InventoryKinds.Autoruns,
                    Id = $@"{prefix}\{subKey}\{valueName}",
                    DisplayName = valueName,
                };
                item.Properties["command"] = key.GetValue(valueName, "", RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString() ?? "";
                if (!subKey.EndsWith("RunOnce", StringComparison.OrdinalIgnoreCase))
                    item.Properties["approved"] = ReadApproved(baseKey, approvedKey, valueName);
                section.Items.Add(item);
            }
        }

        var folders = new (string Label, string Path, RegistryHive Hive)[]
        {
            ("Startup(user)", Environment.GetFolderPath(Environment.SpecialFolder.Startup), RegistryHive.CurrentUser),
            ("Startup(all)", Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), RegistryHive.LocalMachine),
        };
        foreach (var (label, path, hive) in folders)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) continue;
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            foreach (var file in Directory.EnumerateFiles(path))
            {
                var name = Path.GetFileName(file);
                if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                var item = new InventoryItem { Kind = InventoryKinds.Autoruns, Id = $@"{label}\{name}", DisplayName = name };
                item.Properties["file"] = file;
                item.Properties["approved"] = ReadApproved(baseKey, $@"{StartupApproved}\StartupFolder", name);
                section.Items.Add(item);
            }
        }
        return section;
    }

    /// <summary>State in Task Manager &gt; Startup apps (StartupApproved: even byte 0 = enabled).</summary>
    private static string ReadApproved(RegistryKey baseKey, string subKey, string name)
    {
        using var key = baseKey.OpenSubKey(subKey);
        if (key?.GetValue(name) is byte[] { Length: > 0 } data)
            return (data[0] & 1) == 0 ? "enabled" : "disabled";
        return "enabled";
    }

    internal static string Describe(InventoryItem item)
    {
        var sb = new StringBuilder();
        foreach (var (k, v) in item.Properties) sb.Append(k).Append('=').Append(v).Append(' ');
        return sb.ToString().TrimEnd();
    }
}
