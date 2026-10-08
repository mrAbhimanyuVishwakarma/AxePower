using System;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace AxePower.Core.Services.LanDrop;

public enum NetworkSecurityCategory
{
    Private,
    Public,
    Domain,
    Unknown
}

public sealed class NetworkClassificationResult
{
    public required NetworkSecurityCategory Category { get; init; }
    public required bool IsPublicOrUnclassified { get; init; }
    public required string NetworkName { get; init; }
    public string? WarningMessage { get; init; }
}

/// <summary>
/// Classifies the Windows network profile (Private, Public, Domain) of the adapter that owns the
/// address LAN Drop serves on. The answer comes from the Network List Manager for that adapter's
/// live connection, not from the list of every network the PC has ever joined. When Windows
/// cannot say, the result is Unknown, which callers treat the same as Public.
/// </summary>
public static class NetworkProfileClassifier
{
    public static NetworkClassificationResult ClassifyCurrentNetwork(string? selectedIp = null)
    {
        try
        {
            if (string.IsNullOrEmpty(selectedIp) || !IPAddress.TryParse(selectedIp, out var ip))
            {
                return Unclassified("Unclassified Network",
                    "⚠️ Unclassified Network: No network adapter is selected, so its Windows network profile cannot be checked.");
            }

            // Loopback never leaves this PC.
            if (IPAddress.IsLoopback(ip))
            {
                return new NetworkClassificationResult
                {
                    Category = NetworkSecurityCategory.Private,
                    IsPublicOrUnclassified = false,
                    NetworkName = "Local Loopback",
                    WarningMessage = null
                };
            }

            var adapterId = FindAdapterIdForAddress(ip);
            if (adapterId is not null && OperatingSystem.IsWindows())
            {
                var category = TryGetCategoryForAdapter(adapterId.Value, out var networkName);
                if (category is not null) return FromCategory(category.Value, networkName ?? "Network");
            }

            // A routable address is public whatever Windows would have said about it.
            if (!IsPrivateSubnet(ip))
            {
                return new NetworkClassificationResult
                {
                    Category = NetworkSecurityCategory.Public,
                    IsPublicOrUnclassified = true,
                    NetworkName = "Public Network Interface",
                    WarningMessage = "⚠️ Public IP Interface: Your device is exposing LAN Drop on a public IP address."
                };
            }

            return Unclassified("Unclassified Network",
                "⚠️ Unclassified Network: Windows could not report this network's profile. Ensure you are on a trusted Wi-Fi network.");
        }
        catch
        {
            // Fails safe: an unknown network counts as untrusted, so LAN Drop will not start without the override.
            return Unclassified("Unknown Network",
                "⚠️ Network classification unavailable. Verify your Wi-Fi is private before transferring sensitive files.");
        }
    }

    private static NetworkClassificationResult FromCategory(NetworkSecurityCategory category, string networkName) => category switch
    {
        NetworkSecurityCategory.Public => new NetworkClassificationResult
        {
            Category = NetworkSecurityCategory.Public,
            IsPublicOrUnclassified = true,
            NetworkName = networkName,
            WarningMessage = $"⚠️ Public Network ({networkName}): Windows treats this network as Public, so other devices on it are untrusted. Sharing on it needs your permission for this session."
        },
        _ => new NetworkClassificationResult
        {
            Category = category,
            IsPublicOrUnclassified = false,
            NetworkName = networkName,
            WarningMessage = null
        }
    };

    private static NetworkClassificationResult Unclassified(string name, string warning) => new()
    {
        Category = NetworkSecurityCategory.Unknown,
        IsPublicOrUnclassified = true,
        NetworkName = name,
        WarningMessage = warning
    };

    /// <summary>The adapter GUID of the up interface holding this unicast address, if any.</summary>
    private static Guid? FindAdapterIdForAddress(IPAddress ip)
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (!ni.GetIPProperties().UnicastAddresses.Any(a => a.Address.Equals(ip))) continue;
            return Guid.TryParse(ni.Id, out var id) ? id : null;
        }
        return null;
    }

    /// <summary>Whether the address is assigned to an up interface on this PC.</summary>
    public static bool IsLocalAddress(IPAddress ip) =>
        IPAddress.IsLoopback(ip) ||
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(ni => ni.OperationalStatus == OperationalStatus.Up)
            .Any(ni => ni.GetIPProperties().UnicastAddresses.Any(a => a.Address.Equals(ip)));

    private static NetworkSecurityCategory? TryGetCategoryForAdapter(Guid adapterId, out string? networkName)
    {
        networkName = null;
        object? manager = null;
        try
        {
            manager = new NetworkListManagerClass();
            var nlm = (INetworkListManager)manager;
            var connections = nlm.GetNetworkConnections();
            var batch = new INetworkConnection[1];
            while (connections.Next(1, batch, out uint fetched) == 0 && fetched == 1)
            {
                var connection = batch[0];
                try
                {
                    if (connection.GetAdapterId() != adapterId) continue;

                    var network = connection.GetNetwork();
                    networkName = network.GetName();
                    return network.GetCategory() switch
                    {
                        0 => NetworkSecurityCategory.Public,
                        1 => NetworkSecurityCategory.Private,
                        2 => NetworkSecurityCategory.Domain,
                        _ => NetworkSecurityCategory.Unknown
                    };
                }
                finally
                {
                    Marshal.ReleaseComObject(connection);
                }
            }
            return null;
        }
        catch
        {
            // Fails safe: no profile means Unclassified, which is treated as untrusted.
            return null;
        }
        finally
        {
            if (manager is not null) Marshal.ReleaseComObject(manager);
        }
    }

    public static bool IsPrivateSubnet(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] bytes = ip.GetAddressBytes();

            // 10.0.0.0/8
            if (bytes[0] == 10) return true;

            // 172.16.0.0/12 (172.16.x.x - 172.31.x.x)
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;

            // 192.168.0.0/16
            if (bytes[0] == 192 && bytes[1] == 168) return true;

            // 169.254.0.0/16 (link-local)
            if (bytes[0] == 169 && bytes[1] == 254) return true;
        }

        return false;
    }

    // Network List Manager (netlistmgr.h). Only the methods used here are called, but every
    // method before them is declared so the vtable slots line up.

    [ComImport, Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B")]
    private class NetworkListManagerClass { }

    [ComImport, Guid("DCB00000-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetworkListManager
    {
        [return: MarshalAs(UnmanagedType.IUnknown)] object GetNetworks(int flags);
        INetwork GetNetwork(Guid networkId);
        IEnumNetworkConnections GetNetworkConnections();
    }

    [ComImport, Guid("DCB00006-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IEnumNetworkConnections
    {
        [return: MarshalAs(UnmanagedType.IUnknown)] object GetNewEnum();

        [PreserveSig]
        int Next(uint celt,
            [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] INetworkConnection[] rgelt,
            out uint fetched);
    }

    [ComImport, Guid("DCB00005-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetworkConnection
    {
        INetwork GetNetwork();
        bool IsConnectedToInternet { [return: MarshalAs(UnmanagedType.VariantBool)] get; }
        bool IsConnected { [return: MarshalAs(UnmanagedType.VariantBool)] get; }
        int GetConnectivity();
        Guid GetConnectionId();
        Guid GetAdapterId();
    }

    [ComImport, Guid("DCB00002-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetwork
    {
        [return: MarshalAs(UnmanagedType.BStr)] string GetName();
        void SetName([MarshalAs(UnmanagedType.BStr)] string name);
        [return: MarshalAs(UnmanagedType.BStr)] string GetDescription();
        void SetDescription([MarshalAs(UnmanagedType.BStr)] string description);
        Guid GetNetworkId();
        int GetDomainType();
        IEnumNetworkConnections GetNetworkConnections();
        void GetTimeCreatedAndConnected(out uint lowCreated, out uint highCreated, out uint lowConnected, out uint highConnected);
        bool IsConnectedToInternet { [return: MarshalAs(UnmanagedType.VariantBool)] get; }
        bool IsConnected { [return: MarshalAs(UnmanagedType.VariantBool)] get; }
        int GetConnectivity();
        int GetCategory();
    }
}
