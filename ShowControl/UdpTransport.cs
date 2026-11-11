using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Diagnostics;

namespace ShowControl;

internal sealed record UdpReceipt(DateTimeOffset At, string Ip, int SourcePort, string LocalIp, int LocalPort,
    string Text, string Hex, string Disposition, string Source, string List, string Cue, string Group, string Encoding);
internal sealed record UdpContext(string Source, string List, string Cue, string Group);

internal sealed class UdpTransport(ProjectStore store, RuntimeOptions options, ExecutionLogStore logs) : BackgroundService
{
    private readonly object gate = new();
    private readonly Dictionary<IPEndPoint, Listener> listeners = new();
    private readonly Dictionary<IPAddress, DeviceQueue> devices = new();
    private readonly Dictionary<string, UdpReceipt> latest = new();
    private readonly CancellationTokenSource closing = new();
    private long version;
    public long Version => Interlocked.Read(ref version);

    public UdpReceipt? Latest(string ip) { lock (gate) return latest.GetValueOrDefault(ip); }

    public async Task<bool> Exchange(IPAddress address, int port, byte[] bytes, string encoding, NodeCommunication? communication,
        UdpNetwork.Binding? binding, ReplyPolicy response, UdpContext context, Action<string, string> step,
        Func<CancellationToken, Task<bool>>? afterSend, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, closing.Token);
        token = linked.Token;
        var deviceAddress = address;
        DeviceQueue device;
        lock (gate)
        {
            if (!devices.TryGetValue(address, out device!)) devices[address] = device = new();
            device.Users++;
        }
        var acquired = false;
        var sent = false;
        var confirmed = false;
        Listener? sender = null, receiver = null;
        Pending? pending = null;
        try
        {
            if (!device.Serial.Wait(0))
            {
                step("deviceWait", "等待同一 IP 的上一个请求结束");
                await device.Serial.WaitAsync(token);
            }
            acquired = true;
            var cooldown = device.Remaining;
            if (cooldown > TimeSpan.Zero)
            {
                step("deviceWait", $"等待迟到回执隔离窗口 {cooldown.TotalSeconds:0.###} 秒");
                await Task.Delay(cooldown, token);
            }
            var local = UdpNetwork.LocalAddressFor(address, port, binding);
            if (address.IsIPv6LinkLocal && binding is { Index: > 0 }) address = new IPAddress(address.GetAddressBytes(), binding.Index);
            sender = Acquire(local, communication?.SenderPort ?? 0, binding);
            receiver = communication?.ReceiverPort is int receivePort && receivePort != sender.Local.Port
                ? Acquire(local, receivePort, binding) : sender;
            if (response.Mode != "none")
            {
                pending = new(address, communication?.ReplySourcePort, response, context);
                lock (gate) receiver.Pending.Add(pending);
            }
            token.ThrowIfCancellationRequested();
            sent = true;
            await sender.Client.SendAsync(bytes, new IPEndPoint(address, port), token);
            step("send", $"本机 {sender.Local} → {address}:{port} · 接收 {receiver.Local} · {(encoding == "hex" ? "HEX" : $"String ({encoding})")} {Display(bytes, encoding)} · HEX {Convert.ToHexString(bytes)}" +
                (response.Mode == "none" ? " · 不等待回执" : ""));
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
            if (pending is not null) wait.CancelAfter(response.TimeoutMs);
            if (afterSend is not null && !await afterSend(wait.Token)) return false;
            if (pending is null) { confirmed = true; return true; }
            step("wait", $"等待 {response.Mode} 回执 {response.Text} · {response.TimeoutMs} ms");
            await pending.Completion.Task.WaitAsync(wait.Token);
            confirmed = true;
            return true;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            step("failed", $"等待回执 {response.Text} 超时");
            return false;
        }
        finally
        {
            lock (gate)
            {
                if (pending is not null) receiver?.Pending.Remove(pending);
                if (sent && !confirmed)
                {
                    device.CooldownStarted = Stopwatch.GetTimestamp();
                    device.CooldownMs = response.TimeoutMs;
                }
            }
            if (receiver is not null && receiver != sender) Release(receiver);
            if (sender is not null) Release(sender);
            if (acquired) device.Serial.Release();
            lock (gate)
            {
                device.Users--;
                if (device.Users == 0 && device.Remaining <= TimeSpan.Zero)
                    devices.Remove(deviceAddress);
            }
        }
    }

    private Listener Acquire(IPAddress address, int port, UdpNetwork.Binding? binding)
    {
        lock (gate)
        {
            closing.Token.ThrowIfCancellationRequested();
            var key = new IPEndPoint(address, port);
            if (!listeners.TryGetValue(key, out var listener))
            {
                listener = new(key, UdpNetwork.Bind(address, port, binding)) { Keep = port != 0 };
                listeners.Add(key, listener);
                listener.ReceiveTask = Receive(listener);
            }
            listener.Users++;
            return listener;
        }
    }

    private void Release(Listener listener)
    {
        lock (gate)
        {
            listener.Users--;
            if (listener.Users == 0 && !listener.Keep) Close(listener);
        }
    }

    private void Close(Listener listener)
    {
        if (listeners.GetValueOrDefault(listener.Key) == listener) listeners.Remove(listener.Key);
        listener.Client.Dispose();
    }

    private async Task Receive(Listener listener)
    {
        try
        {
            while (!closing.IsCancellationRequested)
            {
                UdpReceiveResult packet;
                try { packet = await listener.Client.ReceiveAsync(closing.Token); }
                catch (SocketException error) when (error.SocketErrorCode == SocketError.ConnectionReset) { continue; }
                lock (gate)
                {
                    var ip = packet.RemoteEndPoint.Address;
                    var pending = listener.Pending.FirstOrDefault(item => !item.Completion.Task.IsCompleted && item.Address.Equals(ip) &&
                        (item.SourcePort is null || item.SourcePort == packet.RemoteEndPoint.Port));
                    var matched = pending is not null && DispatchPlan.Matches(packet.Buffer, pending.Response);
                    var context = pending?.Context ?? new UdpContext("udp", "", "", "");
                    var node = store.Get().Project.IpGroups.SelectMany(group => group.Nodes).FirstOrDefault(node => node.Ip == ip.ToString());
                    var encoding = pending is not null && pending.Response.Mode != "hex" ? pending.Response.Encoding
                        : node?.Communication?.ReceiveEncoding ?? "utf8";
                    var receipt = new UdpReceipt(DateTimeOffset.Now, ip.ToString(), packet.RemoteEndPoint.Port,
                        listener.Local.Address.ToString(), listener.Local.Port, Display(packet.Buffer, encoding),
                        Convert.ToHexString(packet.Buffer), matched ? "matched" : pending is null ? "unsolicited" : "mismatch",
                        context.Source, context.List, context.Cue, context.Group, encoding);
                    var disposition = matched ? "匹配回执" : pending is null ? "主动或未关联回传" : "内容不匹配";
                    if (node is not null)
                    {
                        latest[receipt.Ip] = receipt;
                        Interlocked.Increment(ref version);
                    }
                    logs.Append(new(receipt.At, context.Source, context.List, context.Cue, matched ? "ack" : "receive",
                        $"{context.Group} · 收到 {receipt.Ip}:{receipt.SourcePort} → {receipt.LocalIp}:{receipt.LocalPort} · {disposition} · String ({encoding}) {receipt.Text} · HEX {receipt.Hex}", receipt));
                    if (matched) pending!.Completion.TrySetResult();
                }
            }
        }
        catch (Exception error) when (error is SocketException or ObjectDisposedException or OperationCanceledException)
        {
            lock (gate)
            {
                foreach (var pending in listener.Pending)
                    pending.Completion.TrySetException(new IOException("UDP 监听已关闭，请检查本机地址与端口。", error));
                Close(listener);
            }
        }
    }

    private static string Display(byte[] bytes, string encoding)
    {
        if (encoding == "hex") return Convert.ToHexString(bytes);
        try { return ProjectRules.TextEncoding(encoding).GetString(bytes); }
        catch (DecoderFallbackException) { return "（内容无法按所选 String 编码解码，请查看 HEX）"; }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            do
            {
                if (options.Live) Maintain();
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            closing.Cancel();
            Task[] tasks;
            lock (gate)
            {
                tasks = listeners.Values.Select(listener => listener.ReceiveTask).ToArray();
                foreach (var listener in listeners.Values.ToArray()) Close(listener);
            }
            await Task.WhenAll(tasks);
        }
    }

    private void Maintain()
    {
        var project = store.Get().Project;
        var router = new UdpNetwork.Router(project);
        var desired = new HashSet<IPEndPoint>();
        foreach (var node in project.NodeLibrary ?? [])
        {
            var communication = node.Communication;
            if (communication is null || (communication.SenderPort == 0 && communication.ReceiverPort is null)) continue;
            try
            {
                var address = ProjectRules.ParseIp(node.Ip);
                var adapter = router.Select(address);
                var binding = adapter is null ? null : UdpNetwork.Resolve(adapter, address.AddressFamily);
                if (adapter is not null && binding is null) continue;
                var local = UdpNetwork.LocalAddressFor(address, communication.TargetPort ?? node.HealthQuery?.Port ?? 9, binding);
                var ports = new[] { communication.SenderPort, communication.ReceiverPort ?? 0 }.Where(port => port != 0).Distinct();
                foreach (var port in ports)
                {
                    var key = new IPEndPoint(local, port);
                    desired.Add(key);
                    var listener = Acquire(local, port, binding);
                    lock (gate) listener.Keep = true;
                    Release(listener);
                }
            }
            catch (SocketException) { /* A later pass retries an unavailable address or occupied port. */ }
        }
        lock (gate)
        {
            foreach (var listener in listeners.Values.ToArray())
            {
                listener.Keep = desired.Contains(listener.Key);
                if (!listener.Keep && listener.Users == 0) Close(listener);
            }
            var used = project.IpGroups.SelectMany(group => group.Nodes).Select(node => node.Ip).ToHashSet();
            foreach (var ip in latest.Keys.Where(ip => !used.Contains(ip)).ToArray()) latest.Remove(ip);
            foreach (var address in devices.Where(pair => pair.Value.Users == 0 && pair.Value.Remaining <= TimeSpan.Zero).Select(pair => pair.Key).ToArray())
                devices.Remove(address);
        }
    }

    private sealed class DeviceQueue
    {
        public SemaphoreSlim Serial { get; } = new(1);
        public int Users { get; set; }
        public long CooldownStarted { get; set; }
        public int CooldownMs { get; set; }
        public TimeSpan Remaining => TimeSpan.FromMilliseconds(CooldownMs) - Stopwatch.GetElapsedTime(CooldownStarted);
    }
    private sealed class Listener(IPEndPoint key, UdpClient client)
    {
        public IPEndPoint Key { get; } = key;
        public UdpClient Client { get; } = client;
        public IPEndPoint Local { get; } = (IPEndPoint)client.Client.LocalEndPoint!;
        public int Users { get; set; }
        public bool Keep { get; set; }
        public List<Pending> Pending { get; } = new();
        public Task ReceiveTask { get; set; } = Task.CompletedTask;
    }
    private sealed record Pending(IPAddress Address, int? SourcePort, ReplyPolicy Response, UdpContext Context)
    {
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
