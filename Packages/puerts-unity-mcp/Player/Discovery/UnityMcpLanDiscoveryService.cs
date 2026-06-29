using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace PuertsUnityMcp
{
    public sealed class UnityMcpLanDiscoveryService : IDisposable
    {
        private const string MessageTypeAnnounce = "announce";
        private const string MessageTypeQuery = "query";
        private static readonly IPAddress MulticastDiscoveryAddress = IPAddress.Parse("239.255.189.92");
        private readonly Func<UnityMcpHeartbeat> heartbeatProvider;
        private readonly string requiredGroup;
        private readonly bool cacheDiscoveredHeartbeatsToDisk;
        private readonly Queue<ReceivedPacket> pendingPackets = new Queue<ReceivedPacket>();
        private readonly Dictionary<string, UnityMcpHeartbeat> discoveredHeartbeats = new Dictionary<string, UnityMcpHeartbeat>(StringComparer.Ordinal);
        private UdpClient client;
        private Thread receiveThread;
        private volatile bool running;
        private double nextAnnounceTime;
        private string lastAnnouncementJson;
        private bool loggedFirstAnnouncement;
        private bool loggedSendFailure;
        private bool loggedMulticastFailure;

        public UnityMcpLanDiscoveryService(Func<UnityMcpHeartbeat> heartbeatProvider, string nameGroup, bool cacheDiscoveredHeartbeatsToDisk = true)
        {
            this.heartbeatProvider = heartbeatProvider ?? throw new ArgumentNullException(nameof(heartbeatProvider));
            requiredGroup = NormalizeGroup(nameGroup);
            this.cacheDiscoveredHeartbeatsToDisk = cacheDiscoveredHeartbeatsToDisk;
            StartSocket();
            SendQuery();
        }

        public bool IsRunning => running;

        public UnityMcpHeartbeat[] GetDiscoveredHeartbeats()
        {
            lock (discoveredHeartbeats)
            {
                var result = new UnityMcpHeartbeat[discoveredHeartbeats.Count];
                discoveredHeartbeats.Values.CopyTo(result, 0);
                return result;
            }
        }

        public void Tick()
        {
            if (!running)
            {
                return;
            }

            lastAnnouncementJson = BuildAnnouncementJson(heartbeatProvider(), requiredGroup);
            ProcessPendingPackets(32);

            var now = Time.realtimeSinceStartupAsDouble;
            if (now >= nextAnnounceTime)
            {
                SendAnnouncement();
                nextAnnounceTime = now + UnityMcpConstants.DiscoveryIntervalMs / 1000.0;
            }
        }

        public void SendQuery()
        {
            if (!running)
            {
                return;
            }

            var heartbeat = heartbeatProvider();
            var query = new UnityMcpDiscoveryMessage
            {
                protocol = UnityMcpConstants.DiscoveryProtocol,
                messageType = MessageTypeQuery,
                name = ResolveName(heartbeat?.name, heartbeat?.endpointName, heartbeat?.projectName, heartbeat?.endpointId),
                name_group = requiredGroup,
                endpointId = heartbeat?.endpointId,
                endpointKind = heartbeat?.endpointKind
            };
            var sentCount = SendBroadcast(UnityJson.ToJson(query));
            Debug.Log("[UnityMCP] LAN discovery query sent. group=" + requiredGroup + ", port=" + UnityMcpConstants.DiscoveryPort + ", targets=" + sentCount + ".");
        }

        public void Dispose()
        {
            running = false;
            try { client?.Close(); } catch { }
            client = null;
        }

        public static string BuildAnnouncementJson(UnityMcpHeartbeat heartbeat, string nameGroup)
        {
            if (heartbeat == null)
            {
                return string.Empty;
            }

            var group = NormalizeGroup(nameGroup);
            var message = new UnityMcpDiscoveryMessage
            {
                protocol = UnityMcpConstants.DiscoveryProtocol,
                messageType = MessageTypeAnnounce,
                name = ResolveName(heartbeat.name, heartbeat.endpointName, heartbeat.projectName, heartbeat.endpointId),
                name_group = group,
                endpointId = heartbeat.endpointId,
                endpointKind = heartbeat.endpointKind,
                endpointName = heartbeat.endpointName,
                projectRoot = heartbeat.projectRoot,
                projectName = heartbeat.projectName,
                httpUrl = heartbeat.httpUrl,
                port = heartbeat.port,
                platform = heartbeat.platform,
                unityVersion = heartbeat.unityVersion,
                processId = heartbeat.processId,
                isEditor = heartbeat.isEditor || string.Equals(heartbeat.endpointKind, "editor", StringComparison.OrdinalIgnoreCase),
                capabilities = heartbeat.capabilities ?? new UnityMcpCapabilities()
            };
            return UnityJson.ToJson(message);
        }

        public static bool TryBuildHeartbeatFromMessageJson(string json, string requiredNameGroup, string senderAddress, out UnityMcpHeartbeat heartbeat)
        {
            heartbeat = null;
            UnityMcpDiscoveryMessage message;
            try
            {
                message = UnityJson.FromJson<UnityMcpDiscoveryMessage>(json);
            }
            catch
            {
                return false;
            }

            if (message == null
                || !string.Equals(message.protocol, UnityMcpConstants.DiscoveryProtocol, StringComparison.Ordinal)
                || !string.Equals(message.messageType, MessageTypeAnnounce, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(NormalizeGroup(message.name_group), NormalizeGroup(requiredNameGroup), StringComparison.Ordinal)
                || string.IsNullOrEmpty(message.endpointId)
                || string.IsNullOrEmpty(message.endpointKind))
            {
                return false;
            }

            var httpUrl = ResolveReachableHttpUrl(message.httpUrl, message.port, senderAddress);
            heartbeat = new UnityMcpHeartbeat
            {
                endpointId = UnityMcpPaths.SanitizeId(message.endpointId),
                endpointKind = message.endpointKind,
                endpointName = message.endpointName,
                projectRoot = message.projectRoot,
                projectName = message.projectName,
                name = ResolveName(message.name, message.endpointName, message.projectName, message.endpointId),
                name_group = NormalizeGroup(message.name_group),
                processId = message.processId,
                httpUrl = httpUrl,
                port = message.port,
                unityVersion = message.unityVersion,
                platform = message.platform,
                isEditor = message.isEditor || string.Equals(message.endpointKind, "editor", StringComparison.OrdinalIgnoreCase),
                lastUpdatedUtc = DateTime.UtcNow.ToString("o"),
                capabilities = message.capabilities ?? new UnityMcpCapabilities(),
                source = UnityMcpConstants.DiscoverySource
            };
            return true;
        }

        public static string NormalizeGroup(string value)
        {
            return string.IsNullOrEmpty(value) ? "default" : value.Trim();
        }

        public static string ResolveName(params string[] candidates)
        {
            if (candidates != null)
            {
                foreach (var candidate in candidates)
                {
                    if (!string.IsNullOrEmpty(candidate))
                    {
                        return candidate.Trim();
                    }
                }
            }

            return "unity-mcp";
        }

        private void StartSocket()
        {
            try
            {
                client = new UdpClient(AddressFamily.InterNetwork);
                client.ExclusiveAddressUse = false;
                client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                client.Client.Bind(new IPEndPoint(IPAddress.Any, UnityMcpConstants.DiscoveryPort));
                client.EnableBroadcast = true;
                TryJoinMulticastGroup();
                running = true;
                receiveThread = new Thread(ReceiveLoop)
                {
                    IsBackground = true,
                    Name = "PuertsUnityMcpLanDiscovery"
                };
                receiveThread.Start();
                Debug.Log("[UnityMCP] LAN discovery socket started. group=" + requiredGroup + ", port=" + UnityMcpConstants.DiscoveryPort + ", multicast=" + MulticastDiscoveryAddress + ".");
            }
            catch (Exception ex)
            {
                running = false;
                try { client?.Close(); } catch { }
                client = null;
                Debug.LogWarning("[UnityMCP] LAN discovery disabled: " + ex.Message);
            }
        }

        private void ReceiveLoop()
        {
            while (running)
            {
                try
                {
                    var remote = new IPEndPoint(IPAddress.Any, 0);
                    var bytes = client.Receive(ref remote);
                    var json = Encoding.UTF8.GetString(bytes);
                    lock (pendingPackets)
                    {
                        pendingPackets.Enqueue(new ReceivedPacket
                        {
                            json = json,
                            senderAddress = remote.Address.ToString(),
                            senderPort = remote.Port
                        });
                    }
                }
                catch (SocketException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch
                {
                }
            }
        }

        private void ProcessPendingPackets(int maxPackets)
        {
            for (var i = 0; i < maxPackets; i++)
            {
                ReceivedPacket packet;
                lock (pendingPackets)
                {
                    if (pendingPackets.Count == 0)
                    {
                        return;
                    }

                    packet = pendingPackets.Dequeue();
                }

                ProcessPacket(packet);
            }
        }

        private void ProcessPacket(ReceivedPacket packet)
        {
            UnityMcpDiscoveryMessage message;
            try
            {
                message = UnityJson.FromJson<UnityMcpDiscoveryMessage>(packet.json);
            }
            catch
            {
                return;
            }

            if (message == null
                || !string.Equals(message.protocol, UnityMcpConstants.DiscoveryProtocol, StringComparison.Ordinal)
                || !string.Equals(NormalizeGroup(message.name_group), requiredGroup, StringComparison.Ordinal))
            {
                return;
            }

            var self = heartbeatProvider();
            if (self != null && string.Equals(message.endpointId, self.endpointId, StringComparison.Ordinal))
            {
                return;
            }

            if (string.Equals(message.messageType, MessageTypeQuery, StringComparison.OrdinalIgnoreCase))
            {
                Debug.Log("[UnityMCP] LAN discovery query received from " + packet.senderAddress + ":" + packet.senderPort + "; replying.");
                var announcementJson = BuildAnnouncementJson(heartbeatProvider(), requiredGroup);
                lastAnnouncementJson = announcementJson;
                SendTo(packet.senderAddress, packet.senderPort, announcementJson);
                return;
            }

            if (!TryBuildHeartbeatFromMessageJson(packet.json, requiredGroup, packet.senderAddress, out var heartbeat))
            {
                return;
            }

            RememberDiscoveredHeartbeat(heartbeat);
        }

        private void SendAnnouncement()
        {
            if (string.IsNullOrEmpty(lastAnnouncementJson))
            {
                return;
            }

            var sentCount = SendBroadcast(lastAnnouncementJson);
            if (!loggedFirstAnnouncement)
            {
                loggedFirstAnnouncement = true;
                Debug.Log("[UnityMCP] LAN discovery announcement sent. group=" + requiredGroup + ", port=" + UnityMcpConstants.DiscoveryPort + ", targets=" + sentCount + ".");
            }
        }

        private int SendBroadcast(string json)
        {
            if (!running || string.IsNullOrEmpty(json))
            {
                return 0;
            }

            var sentCount = 0;
            var targets = GetDiscoverySendAddresses();
            for (var i = 0; i < targets.Count; i++)
            {
                if (SendTo(targets[i].ToString(), UnityMcpConstants.DiscoveryPort, json))
                {
                    sentCount++;
                }
            }

            return sentCount;
        }

        private bool SendTo(string host, string json)
        {
            return SendTo(host, UnityMcpConstants.DiscoveryPort, json);
        }

        private bool SendTo(string host, int port, string json)
        {
            if (!running || string.IsNullOrEmpty(host) || string.IsNullOrEmpty(json))
            {
                return false;
            }

            try
            {
                if (!IPAddress.TryParse(host, out var address))
                {
                    return false;
                }

                if (port <= 0)
                {
                    port = UnityMcpConstants.DiscoveryPort;
                }

                var bytes = Encoding.UTF8.GetBytes(json);
                client.Send(bytes, bytes.Length, new IPEndPoint(address, port));
                return true;
            }
            catch (Exception ex)
            {
                if (!loggedSendFailure)
                {
                    loggedSendFailure = true;
                    Debug.LogWarning("[UnityMCP] LAN discovery send failed. host=" + host + ", error=" + ex.Message);
                }
                return false;
            }
        }

        private void TryJoinMulticastGroup()
        {
            try
            {
                client.JoinMulticastGroup(MulticastDiscoveryAddress);
            }
            catch (Exception ex)
            {
                if (!loggedMulticastFailure)
                {
                    loggedMulticastFailure = true;
                    Debug.LogWarning("[UnityMCP] LAN discovery multicast join failed. group=" + MulticastDiscoveryAddress + ", error=" + ex.Message);
                }
            }
        }

        private static List<IPAddress> GetDiscoverySendAddresses()
        {
            var addresses = new List<IPAddress>();
            AddUniqueAddress(addresses, IPAddress.Broadcast);
            foreach (var address in GetInterfaceBroadcastAddresses())
            {
                AddUniqueAddress(addresses, address);
            }
            AddUniqueAddress(addresses, MulticastDiscoveryAddress);
            return addresses;
        }

        private static IEnumerable<IPAddress> GetInterfaceBroadcastAddresses()
        {
            NetworkInterface[] interfaces;
            try
            {
                interfaces = NetworkInterface.GetAllNetworkInterfaces();
            }
            catch
            {
                yield break;
            }

            for (var i = 0; i < interfaces.Length; i++)
            {
                var networkInterface = interfaces[i];
                if (networkInterface == null || networkInterface.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                IPInterfaceProperties properties;
                try
                {
                    properties = networkInterface.GetIPProperties();
                }
                catch
                {
                    continue;
                }

                var unicastAddresses = properties.UnicastAddresses;
                for (var j = 0; j < unicastAddresses.Count; j++)
                {
                    var unicast = unicastAddresses[j];
                    if (unicast == null
                        || unicast.Address == null
                        || unicast.Address.AddressFamily != AddressFamily.InterNetwork
                        || IPAddress.IsLoopback(unicast.Address)
                        || unicast.IPv4Mask == null)
                    {
                        continue;
                    }

                    var broadcast = TryGetBroadcastAddress(unicast.Address, unicast.IPv4Mask);
                    if (broadcast != null)
                    {
                        yield return broadcast;
                    }
                }
            }
        }

        private static IPAddress TryGetBroadcastAddress(IPAddress address, IPAddress subnetMask)
        {
            try
            {
                var addressBytes = address.GetAddressBytes();
                var maskBytes = subnetMask.GetAddressBytes();
                if (addressBytes.Length != 4 || maskBytes.Length != 4)
                {
                    return null;
                }

                var broadcastBytes = new byte[4];
                for (var i = 0; i < broadcastBytes.Length; i++)
                {
                    broadcastBytes[i] = (byte)(addressBytes[i] | (maskBytes[i] ^ 255));
                }

                return new IPAddress(broadcastBytes);
            }
            catch
            {
                return null;
            }
        }

        private static void AddUniqueAddress(List<IPAddress> addresses, IPAddress address)
        {
            if (addresses == null || address == null)
            {
                return;
            }

            for (var i = 0; i < addresses.Count; i++)
            {
                if (addresses[i].Equals(address))
                {
                    return;
                }
            }

            addresses.Add(address);
        }

        private void RememberDiscoveredHeartbeat(UnityMcpHeartbeat heartbeat)
        {
            if (heartbeat == null || string.IsNullOrEmpty(heartbeat.endpointId))
            {
                return;
            }

            var isNew = false;
            lock (discoveredHeartbeats)
            {
                isNew = !discoveredHeartbeats.ContainsKey(heartbeat.endpointId);
                discoveredHeartbeats[heartbeat.endpointId] = heartbeat;
            }

            if (isNew)
            {
                Debug.Log("[UnityMCP] LAN discovery endpoint found. id=" + heartbeat.endpointId
                    + ", kind=" + heartbeat.endpointKind
                    + ", name=" + heartbeat.name
                    + ", group=" + heartbeat.name_group
                    + ", httpUrl=" + heartbeat.httpUrl + ".");
            }

            if (cacheDiscoveredHeartbeatsToDisk)
            {
                WriteDiscoveredHeartbeat(heartbeat);
            }
        }

        private static void WriteDiscoveredHeartbeat(UnityMcpHeartbeat heartbeat)
        {
            if (heartbeat == null || string.IsNullOrEmpty(heartbeat.endpointId))
            {
                return;
            }

            var root = string.Equals(heartbeat.endpointKind, "editor", StringComparison.OrdinalIgnoreCase)
                ? UnityMcpPaths.EditorRoot(heartbeat.endpointId)
                : UnityMcpPaths.PlayerRoot(heartbeat.endpointId);
            AtomicFile.WriteJson(Path.Combine(root, UnityMcpConstants.HeartbeatFileName), heartbeat);
        }

        private static string ResolveReachableHttpUrl(string httpUrl, int port, string senderAddress)
        {
            if (string.IsNullOrEmpty(httpUrl))
            {
                return string.IsNullOrEmpty(senderAddress) || port <= 0 ? httpUrl : "http://" + senderAddress + ":" + port;
            }

            if (!Uri.TryCreate(httpUrl, UriKind.Absolute, out var uri))
            {
                return httpUrl;
            }

            if (!IsLoopbackOrWildcardHost(uri.Host))
            {
                return httpUrl.TrimEnd('/');
            }

            if (string.IsNullOrEmpty(senderAddress))
            {
                return httpUrl.TrimEnd('/');
            }

            var builder = new UriBuilder(uri)
            {
                Host = senderAddress
            };
            return builder.Uri.ToString().TrimEnd('/');
        }

        private static bool IsLoopbackOrWildcardHost(string host)
        {
            return string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
                || string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(host, "0.0.0.0", StringComparison.OrdinalIgnoreCase)
                || string.Equals(host, "*", StringComparison.OrdinalIgnoreCase)
                || string.Equals(host, "+", StringComparison.OrdinalIgnoreCase)
                || string.Equals(host, "::", StringComparison.OrdinalIgnoreCase)
                || string.Equals(host, "[::]", StringComparison.OrdinalIgnoreCase);
        }

        private struct ReceivedPacket
        {
            public string json;
            public string senderAddress;
            public int senderPort;
        }
    }

    [Serializable]
    public sealed class UnityMcpDiscoveryMessage
    {
        public string protocol;
        public string messageType;
        public string name;
        public string name_group;
        public string endpointId;
        public string endpointKind;
        public string endpointName;
        public string projectRoot;
        public string projectName;
        public string httpUrl;
        public int port;
        public string platform;
        public string unityVersion;
        public int processId;
        public bool isEditor;
        public UnityMcpCapabilities capabilities = new UnityMcpCapabilities();
    }
}
