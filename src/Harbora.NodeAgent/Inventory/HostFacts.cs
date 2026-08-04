using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Harbora.NodeAgent.Inventory;

/// <summary>Facts about the machine the agent runs on. An interface so tests are not at the mercy of /proc.</summary>
public interface IHostFacts
{
    string Hostname { get; }
    string OsName { get; }
    string OsVersion { get; }
    string KernelVersion { get; }

    /// <summary>Normalised to <c>amd64</c> / <c>arm64</c>; anything else is reported verbatim.</summary>
    string Architecture { get; }

    int CpuCores { get; }
    long TotalMemoryBytes { get; }
    long FreeMemoryBytes { get; }

    LoadAverage Load { get; }

    DiskSpace Disk(string path);
    IReadOnlyList<string> IpAddresses();
    IReadOnlyList<int> ListeningPorts();

    /// <summary>
    /// Stable identifier for this machine. Lets a re-enrollment be recognised as the same host
    /// rather than silently creating a second node that competes for the same containers.
    /// </summary>
    string MachineFingerprint();
}

public readonly record struct LoadAverage(double One, double Five, double Fifteen);

public readonly record struct DiskSpace(long TotalBytes, long FreeBytes);

/// <summary>
/// Reads the host through <c>/proc</c> and <c>/etc/os-release</c>, with graceful fallbacks so the
/// agent is still developable on a machine that has neither.
/// </summary>
public sealed class HostFacts : IHostFacts
{
    private readonly Lazy<(string Name, string Version)> _os = new(ReadOsRelease);

    public string Hostname => Dns.GetHostName();
    public string OsName => _os.Value.Name;
    public string OsVersion => _os.Value.Version;

    public string KernelVersion =>
        ReadFirstLine("/proc/sys/kernel/osrelease") ?? RuntimeInformation.OSDescription;

    public string Architecture => NormaliseArchitecture(RuntimeInformation.OSArchitecture);

    public int CpuCores => Environment.ProcessorCount;

    public long TotalMemoryBytes => ReadMemInfo("MemTotal") ?? GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;

    public long FreeMemoryBytes => ReadMemInfo("MemAvailable") ?? 0;

    public LoadAverage Load
    {
        get
        {
            var line = ReadFirstLine("/proc/loadavg");
            if (line is null) return default;

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) return default;

            return new LoadAverage(ParseDouble(parts[0]), ParseDouble(parts[1]), ParseDouble(parts[2]));
        }
    }

    public DiskSpace Disk(string path)
    {
        try
        {
            // Walk up until a directory that exists: the data root may not be created yet on the
            // very first start, and "I cannot report disk" is a worse answer than "the volume the
            // data root will live on has this much".
            var probe = path;
            while (!Directory.Exists(probe) && Path.GetDirectoryName(probe) is { Length: > 0 } parent)
                probe = parent;

            var drive = new DriveInfo(Directory.Exists(probe) ? probe : "/");
            return new DiskSpace(drive.TotalSize, drive.AvailableFreeSpace);
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return default;
        }
    }

    public IReadOnlyList<string> IpAddresses()
    {
        var addresses = new List<string>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                var address = unicast.Address;

                // Link-local and loopback tell the control plane nothing it can route to.
                if (IPAddress.IsLoopback(address)) continue;
                if (address.IsIPv6LinkLocal || address.IsIPv6Multicast) continue;
                if (address.AddressFamily == AddressFamily.InterNetwork &&
                    address.GetAddressBytes() is [169, 254, ..]) continue;

                addresses.Add(address.ToString());
            }
        }

        return addresses.Distinct(StringComparer.Ordinal).ToList();
    }

    public IReadOnlyList<int> ListeningPorts()
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Select(endpoint => endpoint.Port)
                .Distinct()
                .OrderBy(port => port)
                .ToList();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    public string MachineFingerprint()
    {
        // machine-id is stable across reboots and is not a secret, but it is an identifier — it is
        // hashed rather than sent, so the control plane can match a host without being handed one
        // more thing worth stealing from its database.
        var seed = ReadFirstLine("/etc/machine-id")
                   ?? ReadFirstLine("/var/lib/dbus/machine-id")
                   ?? PrimaryMacAddress()
                   ?? Hostname;

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"harbora-node:{seed}"));
        return Convert.ToHexStringLower(hash);
    }

    internal static string NormaliseArchitecture(System.Runtime.InteropServices.Architecture architecture) =>
        architecture switch
        {
            System.Runtime.InteropServices.Architecture.X64 => "amd64",
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            _ => architecture.ToString().ToLowerInvariant(),
        };

    private static string? PrimaryMacAddress() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                        n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Select(n => n.GetPhysicalAddress().ToString())
            .FirstOrDefault(mac => !string.IsNullOrEmpty(mac));

    private static (string Name, string Version) ReadOsRelease()
    {
        const string path = "/etc/os-release";
        if (!File.Exists(path))
            return (RuntimeInformation.OSDescription, Environment.OSVersion.VersionString);

        string? name = null, version = null;

        foreach (var line in File.ReadLines(path))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0) continue;

            var key = line[..separator];
            var value = line[(separator + 1)..].Trim('"', '\'');

            if (key == "NAME") name = value;
            else if (key == "VERSION_ID") version = value;
            else if (key == "PRETTY_NAME" && name is null) name = value;
        }

        return (name ?? "Linux", version ?? "unknown");
    }

    private static long? ReadMemInfo(string key)
    {
        const string path = "/proc/meminfo";
        if (!File.Exists(path)) return null;

        foreach (var line in File.ReadLines(path))
        {
            if (!line.StartsWith(key, StringComparison.Ordinal)) continue;

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // "MemTotal:  16316412 kB" — the value is in kibibytes.
            if (parts.Length >= 2 && long.TryParse(parts[1], out var kilobytes))
                return kilobytes * 1024;
        }

        return null;
    }

    private static string? ReadFirstLine(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadLines(path).FirstOrDefault()?.Trim() : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static double ParseDouble(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
}
