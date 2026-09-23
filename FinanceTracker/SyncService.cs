using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FinanceTracker.Interop;
using FinanceTracker.Localization;
using FinanceTracker.Models;

namespace FinanceTracker.Services
{
    public static class SyncService
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
        private const int DiscoveryPort = 12346;
        private const int SyncTimeoutMs = 60_000;
        private static readonly IPAddress MulticastGroup = IPAddress.Parse("239.255.123.46");

        // Ограничивает число одновременно обрабатываемых клиентских сессий на сервере.
        private static readonly SemaphoreSlim _clientGate = new(16, 16);

        private class SyncMessage
        {
            /// Клиент сообщает, с какого момента он уже видел данные (для инкрементальной отдачи).
            public HybridTimestamp LastSyncUtc { get; set; } = new HybridTimestamp(0, 0);
            public List<ExpenseItem> Data { get; set; } = new();
        }

        // ====== UDP обнаружение ======
        public static async Task StartDiscoveryListenerAsync(
            Action<string, int> onDeviceDiscovered,
            CancellationToken cancellationToken,
            Action<string>? logCallback = null)
        {
            using var udpClient = new UdpClient(DiscoveryPort);
#if ANDROID
            // На Android Wi-Fi стек в энергосбережении фильтрует broadcast/multicast.
            // Без MulticastLock пакеты до сокета не доходят даже при открытом порте.
            global::Android.Net.Wifi.WifiManager.MulticastLock? multicastLock = null;
            try
            {
                var wifiManager = global::Android.App.Application.Context
                    .GetSystemService(global::Android.Content.Context.WifiService)
                    as global::Android.Net.Wifi.WifiManager;
                if (wifiManager != null)
                {
                    multicastLock = wifiManager.CreateMulticastLock("FinanceTracker");
                    multicastLock.Acquire();
                    logCallback?.Invoke(Localizer.Instance["Sync_MulticastLockCaptured"]);
                }
            }
            catch (Exception ex)
            {
                logCallback?.Invoke(Localizer.Instance.Format("Sync_MulticastLockFailed", ex.Message));
            }
#endif
            try
            {
                logCallback?.Invoke(Localizer.Instance.Format("Sync_ListeningBroadcast", DiscoveryPort));

                // Присоединяемся к multicast-группе: так телефон гарантированно увидит ПК
                // и наоборот даже там, где роутер режет broadcast между Wi-Fi и проводом.
                // Если join не удался (нет multicast-маршрута), продолжаем слушать
                // broadcast/unicast — иначе теряем обнаружение устройств вообще.
                try
                {
                    udpClient.JoinMulticastGroup(MulticastGroup);
                    logCallback?.Invoke(Localizer.Instance.Format("Sync_JoinedMulticast", MulticastGroup));
                }
                catch (Exception ex)
                {
                    try
                    {
                        logCallback?.Invoke(Localizer.Instance.Format("Sync_MulticastJoinFailed", ex.Message));
                    }
                    catch { /* ignore */ }
                }

                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        var result = await udpClient.ReceiveAsync(cancellationToken);
                        string message = Encoding.UTF8.GetString(result.Buffer);
                        var parts = message.Split(':');
                        if (parts.Length == 2 && int.TryParse(parts[1], out int port))
                        {
                            onDeviceDiscovered(parts[0], port);
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        // Сокет мог уйти в неопределённое состояние (Android) — не спамим лог, выходим.
                        try
                        {
                            logCallback?.Invoke(Localizer.Instance.Format("Sync_UdpListenError", ex.Message));
                        }
                        catch { /* ignore */ }
                        break;
                    }
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
#if ANDROID
                multicastLock?.Release();
#endif
            }
        }

        public static async Task StartDiscoveryBroadcasterAsync(
            int tcpPort,
            Action<string>? logCallback = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                await RunDiscoveryBroadcasterAsync(tcpPort, logCallback, cancellationToken);
            }
            catch (Exception ex)
            {
                // Task.Run запускает метод в fire-and-forget: без этого try/catch
                // ошибка старта рассыльщика была бы молча проглочена.
                try { logCallback?.Invoke(Localizer.Instance.Format("Sync_DiscoveryBroadcasterError", ex.Message)); } catch { }
            }
        }

        private static async Task RunDiscoveryBroadcasterAsync(
            int tcpPort,
            Action<string>? logCallback,
            CancellationToken cancellationToken)
        {
            using var udpClient = new UdpClient();
            bool sendBroadcast = true;
            try { udpClient.EnableBroadcast = true; }
            catch (Exception ex)
            {
                // На Android часть платформенных реализаций не даёт поднять SO_BROADCAST.
                // Это не критично: multicast и unicast-зондирование работают без него.
                sendBroadcast = false;
                try { logCallback?.Invoke(Localizer.Instance.Format("Sync_SoBroadcastUnavailable", ex.Message)); } catch { }
            }
            try { udpClient.Ttl = 16; } catch { } // multicast-пакету хватит одного-двух прыжков в LAN
            string localIp;
            try { localIp = GetLocalIPAddress(); }
            catch (Exception ex)
            {
                try { logCallback?.Invoke(Localizer.Instance.Format("Sync_GetLocalIpFailed", ex.Message)); } catch { }
                localIp = "127.0.0.1";
            }
            string message = $"{localIp}:{tcpPort}";
            byte[] data = Encoding.UTF8.GetBytes(message);
            List<IPAddress> targets;
            try
            {
                targets = GetBroadcastTargets();
                if (targets.Count == 0)
                    targets.Add(IPAddress.Broadcast);
            }
            catch (Exception ex)
            {
                logCallback?.Invoke(Localizer.Instance.Format("Sync_BroadcastTargetsFailed", ex.Message));
                targets = new List<IPAddress> { IPAddress.Broadcast };
            }
            var multicastEndPoint = new IPEndPoint(MulticastGroup, DiscoveryPort);
            bool errorLogged = false;

            try
            {
                logCallback?.Invoke(Localizer.Instance.Format(
                    "Sync_DiscoveryAnnounce",
                    localIp, tcpPort, targets.Count, GetSubnetHosts().Count));
            }
            catch { }

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // 1. Directed broadcast по всем интерфейсам (base → 255.255.255.255)
                    if (sendBroadcast)
                    {
                        foreach (var target in targets)
                        {
                            var broadcastEndPoint = new IPEndPoint(target, DiscoveryPort);
                            await udpClient.SendAsync(data, data.Length, broadcastEndPoint);
                        }
                    }

                    // 2. Multicast: роутеры чаще пробрасывают его между Wi-Fi и проводом,
                    //    а на Android MulticastLock гарантирует доставку в сокет.
                    await udpClient.SendAsync(data, data.Length, multicastEndPoint);

                    // 3. Unicast-зондирование подсети (надёжно как обычный IP-пакет):
                    //    пробуем каждый адрес /24 на порту обнаружения. Это работает даже там,
                    //    где роутер полностью режет broadcast/multicast между сегментами.
                    foreach (var host in GetSubnetHosts())
                    {
                        var probeEndPoint = new IPEndPoint(host, DiscoveryPort);
                        await udpClient.SendAsync(data, data.Length, probeEndPoint);
                    }
                    errorLogged = false;
                }
                catch (Exception ex)
                {
                    // Логируем ошибку один раз, чтобы не спамить лог каждые 2 секунды
                    if (!errorLogged)
                    {
                        logCallback?.Invoke(Localizer.Instance.Format("Sync_BroadcastSendError", ex.Message));
                        errorLogged = true;
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }

        private static string GetLocalIPAddress()
        {
            try
            {
                // Отдаём предпочтение реальному LAN-интерфейсу (RFC1918 + DHCP),
                // который доступен другим устройствам, и пропускаем APIPA/виртуальные
                // адаптеры (Radmin VPN, VirtualBox и т.п.), иначе сервер анонсирует
                // недоступный адрес и клиент получает "Connection refused".
                IPAddress? best = null;
                int bestScore = int.MinValue;

                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or
                        NetworkInterfaceType.Tunnel) continue;

                    IPInterfaceProperties props;
                    try { props = ni.GetIPProperties(); }
                    catch { continue; } // на Android часть интерфейсов не отдаёт свойства

                    var ip = props.UnicastAddresses
                        .Select(a => a.Address)
                        .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                    if (ip is null || IPAddress.IsLoopback(ip)) continue;

                    byte[] b = ip.GetAddressBytes();
                    // APIPA (169.254.0.0/16): адрес выдан без DHCP, в сети недоступен
                    if (b[0] == 169 && b[1] == 254) continue;

                    bool privateLan = IsPrivateLanAddress(ip);
                    bool hasIpv4Gateway;
                    try
                    {
                        hasIpv4Gateway = props.GatewayAddresses.Any(
                            g => g.Address is not null &&
                                 g.Address.AddressFamily == AddressFamily.InterNetwork &&
                                 !IPAddress.IsLoopback(g.Address));
                    }
                    catch { hasIpv4Gateway = false; }

                    int score = 0;
                    if (privateLan) score += 2;
                    if (hasIpv4Gateway) score += 1;
                    // Реальный Wi-Fi/Ethernet почти всегда получает адрес по DHCP.
                    if (score >= 2 && IsDhcpEnabled(ni)) score += 1;

                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = ip;
                    }
                }

                return best?.ToString() ?? "127.0.0.1";
            }
            catch
            {
                // Платформенные ошибки NetworkInterface на Android не должны ронять
                // discovery-рассыльщик: вернём loopback, основной канал данных на TCP.
                return "127.0.0.1";
            }
        }

        // Цели для broadcast: directed broadcast каждой реальной IPv4-подсети
        // (например 192.168.0.255). Ограниченный 255.255.255.255 многие роутеры
        // между Wi-Fi и проводным сегментом не пробрасывают, а directed — ходит
        // как обычный пакет. На многоадаптерных машинах так пакет уходит в нужную сеть.
        private static List<IPAddress> GetBroadcastTargets()
        {
            var targets = new List<IPAddress>();

            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or
                    NetworkInterfaceType.Tunnel) continue;

                IPInterfaceProperties props;
                try { props = ni.GetIPProperties(); }
                catch { continue; } // на Android часть интерфейсов не отдаёт свойства

                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(ua.Address)) continue;

                    byte[]? mask;
                    try
                    {
                        if (ua.IPv4Mask is null || ua.IPv4Mask.GetAddressBytes().Length != 4) continue;
                        ua.IPv4Mask.GetAddressBytes(); // прогреваем доступ
                        mask = ua.IPv4Mask.GetAddressBytes();
                    }
                    catch { continue; } // не все интерфейсы знают маску

                    byte[] b = ua.Address.GetAddressBytes();
                    // APIPA (169.254.0.0/16)
                    if (b[0] == 169 && b[1] == 254) continue;

                    byte[] bc = new byte[4];
                    for (int i = 0; i < 4; i++)
                        bc[i] = (byte)(b[i] | ~mask[i]);

                    var target = new IPAddress(bc);
                    if (bc[0] == 255 && bc[1] == 255 && bc[2] == 255 && bc[3] == 255)
                        continue; // ограниченный broadcast 255.255.255.255 добавим отдельно

                    if (!targets.Contains(target))
                        targets.Add(target);
                }
            }

            // Directed broadcast /24 из локального адреса: на Android IPv4Mask у
            // NetworkInterface часто отсутствует, без него не построить 192.168.x.255.
            if (IPAddress.TryParse(GetLocalIPAddress(), out var localIp) &&
                localIp.AddressFamily == AddressFamily.InterNetwork)
            {
                byte[] lb = localIp.GetAddressBytes();
                if (!(lb[0] == 169 && lb[1] == 254))
                {
                    var localBc = new IPAddress(new[] { lb[0], lb[1], lb[2], (byte)255 });
                    if (!targets.Contains(localBc))
                        targets.Add(localBc);
                }
            }

            if (!targets.Contains(IPAddress.Broadcast))
                targets.Add(IPAddress.Broadcast);

            return targets;
        }

        // Адреса хостов подсети для unicast-зондирования. Зондируем только /24 и
        // более узкие подсети (≤254 хостов), чтобы не заливать сеть трафиком и не
        // «стучаться» в чужие внешние диапазоны на виртуальных адаптерах.
        private static List<IPAddress> GetSubnetHosts()
        {
            var hosts = new List<IPAddress>(256);

            // Базовый /24 из локального адреса: на Android (и во многих домах)
            // сеть всегда /24, но IPv4Mask у NetworkInterface на Android часто отсутствует.
            AddLocalSubnetHosts(hosts);

            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or
                    NetworkInterfaceType.Tunnel) continue;

                IPInterfaceProperties props;
                try { props = ni.GetIPProperties(); }
                catch { continue; }

                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(ua.Address)) continue;

                    byte[] mask;
                    try
                    {
                        if (ua.IPv4Mask is null || ua.IPv4Mask.GetAddressBytes().Length != 4) continue;
                        mask = ua.IPv4Mask.GetAddressBytes();
                    }
                    catch { continue; }

                    byte[] b = ua.Address.GetAddressBytes();
                    // APIPA (169.254.0.0/16)
                    if (b[0] == 169 && b[1] == 254) continue;

                    if (mask[0] != 255 || mask[1] != 255 || mask[2] != 255 || mask[3] != 255) continue;

                    for (int h = 1; h <= 254; h++)
                    {
                        if (h == b[3]) continue; // свой адрес уже анонсирован отдельно

                        var candidate = new IPAddress(new[]
                        {
                            (byte)(b[0] & mask[0]),
                            (byte)(b[1] & mask[1]),
                            (byte)(b[2] & mask[2]),
                            (byte)h
                        });
                        if (!hosts.Contains(candidate))
                            hosts.Add(candidate);
                    }
                }
            }

            return hosts;
        }

        // Заполняет hosts адресами /24, вычисленной из GetLocalIPAddress().
        private static void AddLocalSubnetHosts(List<IPAddress> hosts)
        {
            if (!IPAddress.TryParse(GetLocalIPAddress(), out var ip)) return;
            if (ip.AddressFamily != AddressFamily.InterNetwork) return;
            byte[] b = ip.GetAddressBytes();
            if (b[0] == 169 && b[1] == 254) return;

            for (int h = 1; h <= 254; h++)
            {
                if (h == b[3]) continue;
                var candidate = new IPAddress(new[] { b[0], b[1], b[2], (byte)h });
                if (!hosts.Contains(candidate))
                    hosts.Add(candidate);
            }
        }

        private static bool IsPrivateLanAddress(IPAddress ip)
        {
            byte[] b = ip.GetAddressBytes();
            if (b[0] == 10) return true;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
            if (b[0] == 192 && b[1] == 168) return true;
            return false;
        }

        private static bool IsDhcpEnabled(NetworkInterface ni)
        {
            if (!OperatingSystem.IsWindows()) return false;
            try
            {
                return ni.GetIPProperties().GetIPv4Properties().IsDhcpEnabled;
            }
            catch (Exception)
            {
                // На части интерфейсов IPv4-свойства недоступны — считаем без DHCP.
                return false;
            }
        }

        // ====== TCP сервер ======
        public static async Task StartServerAsync(
            int port,
            Action<string> logCallback,
            Action<string>? deviceCallback = null,
            Action? onDataReceived = null,
            CancellationToken cancellationToken = default)
        {
            TcpListener? listener = null;
            try
            {
                listener = new TcpListener(IPAddress.Any, port);
                listener.Start();
                logCallback(Localizer.Instance.Format("Sync_ServerStarted", port));

                while (!cancellationToken.IsCancellationRequested)
                {
                    TcpClient client;
                    try
                    {
                        client = await listener.AcceptTcpClientAsync(cancellationToken);
                    }
                    catch (OperationCanceledException) { break; }

                    // Каждый клиент обрабатывается в отдельной задаче,
                    // чтобы долгая синхронизация не блокировала приём следующих.
                    _ = HandleClientAsync(
                        client, logCallback, deviceCallback, onDataReceived,
                        cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                logCallback(Localizer.Instance["Sync_ServerStopped"]);
            }
            catch (Exception ex)
            {
                logCallback(Localizer.Instance.Format("Sync_ServerError", ex.Message));
            }
            finally
            {
                listener?.Stop();
            }
        }

        private static async Task HandleClientAsync(
            TcpClient client,
            Action<string> logCallback,
            Action<string>? deviceCallback,
            Action? onDataReceived,
            CancellationToken ct)
        {
            // Линкуем CTS клиента с серверным: при остановке сервера все активные
            // сессии прерываются, а не висят до собственного таймаута.
            using var clientCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // Гарантированно высвобождаем сокет, даже если отмена случилась до входа в core
            // (когда конструкция using захватывает клиента и тогда, и там — повторный Dispose безопасен).
            using var clientRef = client;
            try
            {
                await _clientGate.WaitAsync(clientCts.Token);
                try
                {
                    await HandleClientCoreAsync(
                        client, logCallback, deviceCallback, onDataReceived, clientCts.Token);
                }
                finally
                {
                    _clientGate.Release();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                try { logCallback(Localizer.Instance.Format("Sync_ClientError", ex.Message)); } catch { /* ignore */ }
            }
        }

        private static async Task HandleClientCoreAsync(
            TcpClient client,
            Action<string> logCallback,
            Action<string>? deviceCallback,
            Action? onDataReceived,
            CancellationToken ct)
        {
            using (client)
            using (var stream = client.GetStream())
            {
                if (client.Client.RemoteEndPoint is IPEndPoint remoteEndPoint)
                    deviceCallback?.Invoke($"{remoteEndPoint.Address}:{remoteEndPoint.Port}");

                logCallback(Localizer.Instance["Sync_ClientConnected"]);

                // 1. HELLO (клиент присылает свой lastSync, чтобы сервер знал, что отдавать)
                var hello = await ReceiveMessageAsync(stream, ct);

                // 2. MY DATA (клиент присылает свои записи, включая tombstone)
                var incoming = await ReceiveMessageAsync(stream, ct);
                logCallback(Localizer.Instance.Format("Sync_ReceivedRecords", incoming.Data.Count));

                // 3. Merge + подготовка ответа атомарны внутри NativeMethods
                //    (несколько клиентов параллельно не перемежают чтение с записью).
                var responseData = NativeMethods.MergeAndGetSince(hello.LastSyncUtc, incoming.Data);
                logCallback(Localizer.Instance["Sync_LocalDataUpdated"]);

                // 4. YOUR DATA (сервер отдаёт только то, что новее lastSync клиента)
                var outgoing = new SyncMessage { Data = responseData };
                await SendMessageAsync(stream, outgoing, ct);
                logCallback(Localizer.Instance.Format("Sync_SentRecords", responseData.Count));

                // 5. Минимальный ACK от клиента
                await ReceiveAckAsync(stream, ct);

                try { onDataReceived?.Invoke(); } catch { /* ignore */ }
            }
        }
        public static async Task StartClientAsync(
    string serverIp,
    int port,
    Action<string> logCallback,
    HybridTimestamp lastSyncUtc = default,
    Action? onDataReceived = null,
    CancellationToken cancellationToken = default)
        {
            try
            {
                logCallback(Localizer.Instance.Format("Sync_ConnectingTo", serverIp, port));
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Parse(serverIp), port, cancellationToken);
                using var stream = client.GetStream();

                // Эффективная метка: переданная вызывающим, либо сохранённая в БД,
                // либо 0:0 (первая синхронизация — сервер отдаст всё).
                var effectiveLastSync = lastSyncUtc != default
                    ? lastSyncUtc
                    : NativeMethods.GetLastSyncTime() ?? default;

                // 1. HELLO
                await SendMessageAsync(stream, new SyncMessage
                {
                    LastSyncUtc = effectiveLastSync,
                    Data = new List<ExpenseItem>()
                }, cancellationToken);

                // 2. MY DATA (все локальные записи, включая tombstone)
                var myData = NativeMethods.GetAllExpenses();
                await SendMessageAsync(stream, new SyncMessage { Data = myData }, cancellationToken);
                logCallback(Localizer.Instance.Format("Sync_SentRecords", myData.Count));

                // 3. YOUR DATA
                var response = await ReceiveMessageAsync(stream, cancellationToken);
                logCallback(Localizer.Instance.Format("Sync_ReceivedRecords", response.Data.Count));

                // 4. merge ответа сервера (внутри NativeMethods — единая блокировка)
                if (response.Data.Count > 0)
                {
                    NativeMethods.MergeExpenses(response.Data);
                    logCallback(Localizer.Instance["Sync_LocalDataUpdated"]);
                }

                // 4.1. Двигаем водяной знак lastSync вперёд (для инкрементальных обновлений):
                //     учитываем и свои отправленные, и полученные записи.
                var seen = new List<ExpenseItem>(myData.Count + response.Data.Count);
                seen.AddRange(myData);
                seen.AddRange(response.Data);
                NativeMethods.AdvanceLastSync(seen);

                // 5. Минимальный ACK: 1 байт — «принял и обработал»
                await SendAckAsync(stream, cancellationToken);

                try { onDataReceived?.Invoke(); } catch { /* ignore */ }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                logCallback(Localizer.Instance.Format("Sync_ClientError", ex.Message));
            }
        }

        // ====== Вспомогательные методы (с CancellationToken + таймаутом) ======

        // Явный big-endian префикс длины: не зависит от архитектуры хоста.
        private static byte[] GetLengthPrefix(int length) => new[]
        {
            (byte)(length >> 24), (byte)(length >> 16), (byte)(length >> 8), (byte)length
        };

        private static int ReadLengthPrefix(byte[] buffer) =>
            (buffer[0] << 24) | (buffer[1] << 16) | (buffer[2] << 8) | buffer[3];

        private static async Task SendMessageAsync(NetworkStream stream, SyncMessage message, CancellationToken ct)
        {
            string json = JsonSerializer.Serialize(message, JsonOptions);
            byte[] data = Encoding.UTF8.GetBytes(json);
            byte[] lengthPrefix = GetLengthPrefix(data.Length);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(SyncTimeoutMs);
            try
            {
                await stream.WriteAsync(lengthPrefix.AsMemory(0, 4), timeoutCts.Token);
                await stream.WriteAsync(data.AsMemory(0, data.Length), timeoutCts.Token);
                await stream.FlushAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("Sync send timed out");
            }
        }

        private static async Task<SyncMessage> ReceiveMessageAsync(NetworkStream stream, CancellationToken ct)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(SyncTimeoutMs);
            try
            {
                byte[] lengthBuffer = new byte[4];
                await ReadExactlyAsync(stream, lengthBuffer, 4, timeoutCts.Token);
                int length = ReadLengthPrefix(lengthBuffer);
                if (length < 0 || length > 100 * 1024 * 1024)
                    throw new IOException("Invalid message length: " + length);

                byte[] dataBuffer = new byte[length];
                await ReadExactlyAsync(stream, dataBuffer, length, timeoutCts.Token);
                string json = Encoding.UTF8.GetString(dataBuffer);
                return JsonSerializer.Deserialize<SyncMessage>(json, JsonOptions) ?? new SyncMessage();
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("Sync receive timed out");
            }
        }

        private static async Task SendAckAsync(NetworkStream stream, CancellationToken ct)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(SyncTimeoutMs);
            try
            {
                byte[] ack = { 0x01 };
                await stream.WriteAsync(ack.AsMemory(0, 1), timeoutCts.Token);
                await stream.FlushAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("Sync ack timed out");
            }
        }

        private static async Task ReceiveAckAsync(NetworkStream stream, CancellationToken ct)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(SyncTimeoutMs);
            try
            {
                byte[] buf = new byte[1];
                await ReadExactlyAsync(stream, buf, 1, timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("Sync ack timed out");
            }
        }

        private static async Task ReadExactlyAsync(NetworkStream stream, byte[] buffer, int count, CancellationToken ct)
        {
            int offset = 0;
            while (offset < count)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), ct);
                if (read == 0)
                    throw new IOException(Localizer.Instance["Sync_ConnectionClosed"]);
                offset += read;
            }
        }
    }
}