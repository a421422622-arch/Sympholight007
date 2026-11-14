using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ShowControl;

internal static class UdpNetwork
{
    private const SocketOptionName WindowsUnicastInterface = (SocketOptionName)31;

    public static InterfaceEntry[] Interfaces(Project project, bool includeInactive = false)
    {
        var selected = project.SelectedUdpInterfaces.ToHashSet(StringComparer.Ordinal);
        return NetworkInterface.GetAllNetworkInterfaces().Select(adapter =>
        {
            var addresses = ReadAddresses(adapter);
            var loopback = adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback;
            var up = adapter.OperationalStatus == OperationalStatus.Up;
            var usable = addresses.Any(item => item.Address.AddressFamily == AddressFamily.InterNetwork ||
                !item.Address.IsIPv6LinkLocal);
            if (!includeInactive && !loopback && !selected.Contains(adapter.Id) && (!up || !usable))
                return null;
            var networks = addresses.Where(item => item.PrefixLength >= 0 && item.PrefixLength <= item.Address.GetAddressBytes().Length * 8)
                .Select(item => NetworkOf(item.Address, item.PrefixLength).ToString()).Distinct().ToArray();
            return new InterfaceEntry(adapter.Id, loopback ? "本机回环" : adapter.Name, up,
                addresses.Select(item => item.Address.ToString()).ToArray(), networks, loopback);
        }).OfType<InterfaceEntry>().OrderByDescending(adapter => adapter.Up && adapter.Addresses.Length > 0)
            .ThenBy(adapter => adapter.Name).ToArray();
    }

    public static Binding? Resolve(string id, AddressFamily family)
    {
        try
        {
            var adapter = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(item => item.Id == id);
            if (adapter is null || adapter.OperationalStatus != OperationalStatus.Up)
                return null;
            var addresses = ReadAddresses(adapter).Where(address => address.Address.AddressFamily == family).ToArray();
            if (addresses.Length == 0)
                return null;
            var properties = adapter.GetIPProperties();
            var index = family == AddressFamily.InterNetwork
                ? properties.GetIPv4Properties()?.Index : properties.GetIPv6Properties()?.Index;
            return new Binding(index ?? 0, addresses);
        }
        catch (NetworkInformationException) { return null; }
    }

    public static async Task<Binding> Wait(string id, AddressFamily family, Action? waiting, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var binding = Resolve(id, family);
            if (binding is not null)
                return binding;
            waiting?.Invoke();
            waiting = null;
            await Task.Delay(1000, token);
        }
    }

    public static UdpClient Connect(IPAddress remote, int port, Binding? binding)
    {
        var client = new UdpClient(remote.AddressFamily);
        try
        {
            if (binding is not null)
            {
                var local = binding.Addresses.OrderByDescending(address => MatchesPrefix(address, remote) ? address.PrefixLength : -1)
                    .ThenBy(address => address.Address.IsIPv6LinkLocal != remote.IsIPv6LinkLocal)
                    .First().Address;
                if (OperatingSystem.IsWindows() && binding.Index > 0)
                {
                    var ipv4 = remote.AddressFamily == AddressFamily.InterNetwork;
                    client.Client.SetSocketOption(ipv4 ? SocketOptionLevel.IP : SocketOptionLevel.IPv6,
                        WindowsUnicastInterface, ipv4 ? IPAddress.HostToNetworkOrder(binding.Index) : binding.Index);
                }
                client.Client.Bind(new IPEndPoint(local, 0));
                if (remote.IsIPv6LinkLocal && binding.Index > 0)
                    remote = new IPAddress(remote.GetAddressBytes(), binding.Index);
            }
            client.Connect(remote, port);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public static IPAddress LocalAddressFor(IPAddress remote, int port, Binding? binding)
    {
        if (binding is not null)
            return binding.Addresses.OrderByDescending(address => MatchesPrefix(address, remote) ? address.PrefixLength : -1)
                .ThenBy(address => address.Address.IsIPv6LinkLocal != remote.IsIPv6LinkLocal).First().Address;
        using var route = new Socket(remote.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        route.Connect(new IPEndPoint(remote, port));
        return ((IPEndPoint)route.LocalEndPoint!).Address;
    }

    public static UdpClient Bind(IPAddress local, int port, Binding? binding)
    {
        var client = new UdpClient(local.AddressFamily);
        try
        {
            client.ExclusiveAddressUse = true;
            if (OperatingSystem.IsWindows() && binding is { Index: > 0 })
            {
                var ipv4 = local.AddressFamily == AddressFamily.InterNetwork;
                client.Client.SetSocketOption(ipv4 ? SocketOptionLevel.IP : SocketOptionLevel.IPv6,
                    WindowsUnicastInterface, ipv4 ? IPAddress.HostToNetworkOrder(binding.Index) : binding.Index);
            }
            client.Client.Bind(new IPEndPoint(local, port));
            return client;
        }
        catch { client.Dispose(); throw; }
    }

    private static LocalAddress[] ReadAddresses(NetworkInterface adapter)
    {
        try
        {
            return adapter.GetIPProperties().UnicastAddresses
                .Where(item => !item.Address.Equals(IPAddress.Any) && !item.Address.Equals(IPAddress.IPv6Any))
                .Select(item => new LocalAddress(item.Address, item.PrefixLength)).ToArray();
        }
        catch (NetworkInformationException) { return []; }
    }

    private static bool MatchesPrefix(LocalAddress local, IPAddress remote)
    {
        var bytes = local.Address.GetAddressBytes();
        var target = remote.GetAddressBytes();
        var prefix = local.PrefixLength;
        if (bytes.Length != target.Length || prefix < 0 || prefix > bytes.Length * 8)
            return false;
        var whole = prefix / 8;
        var remainder = prefix % 8;
        return bytes.AsSpan(0, whole).SequenceEqual(target.AsSpan(0, whole)) &&
            (remainder == 0 || ((bytes[whole] ^ target[whole]) >> (8 - remainder)) == 0);
    }

    public static IPNetwork ParseSubnet(string network)
    {
        var parts = network?.Split('/');
        if (parts is not { Length: 2 } || !IPAddress.TryParse(parts[0], out var address) ||
            address.IsIPv4MappedToIPv6 || (address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0) ||
            !int.TryParse(parts[1], out var prefix) || prefix < 0 || prefix > address.GetAddressBytes().Length * 8)
            throw new ArgumentException($"UDP 网段「{network}」须使用 CIDR 格式，例如 192.168.10.0/24。");
        return NetworkOf(address, prefix);
    }

    private static IPNetwork NetworkOf(IPAddress address, int prefix)
    {
        var bytes = address.GetAddressBytes();
        for (var i = 0; i < bytes.Length; i++)
        {
            var bits = Math.Clamp(prefix - i * 8, 0, 8);
            bytes[i] &= (byte)(0xff << (8 - bits));
        }
        return new IPNetwork(new IPAddress(bytes), prefix);
    }

    internal sealed class Router(Project project)
    {
        private readonly (IPNetwork Network, string Adapter)[] routes = (project.UdpRoutes ?? [])
            .Select(route => (Network: ParseSubnet(route.Network), Adapter: route.InterfaceId))
            .OrderByDescending(route => route.Network.PrefixLength).ToArray();
        private readonly string? defaultAdapter = project.UdpInterfaceId ?? project.UdpProjectInterfaceIds?.FirstOrDefault();

        public string? Select(IPAddress destination)
        {
            if (IPAddress.IsLoopback(destination))
                return null;
            foreach (var route in routes)
                if (route.Network.BaseAddress.AddressFamily == destination.AddressFamily && route.Network.Contains(destination))
                    return route.Adapter;
            return defaultAdapter;
        }
    }

    internal sealed record InterfaceEntry(string Id, string Name, bool Up, string[] Addresses, string[] Networks, bool Loopback);
    internal sealed record LocalAddress(IPAddress Address, int PrefixLength);
    internal sealed record Binding(int Index, LocalAddress[] Addresses);
}
