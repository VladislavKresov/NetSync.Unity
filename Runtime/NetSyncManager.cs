using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using NetSync;
using NetSync.Discovery;
using NetSync.Peers;
using NetSync.Transports;
using UnityEngine;

namespace NetSync.Unity {
    /// <summary>
    /// Scene-facing facade over the NetSync core: drop it on a GameObject, pick a role,
    /// press play. Replaces the 1.x Network/Client/Server MonoBehaviours.
    ///
    /// All events fire on the Unity main thread (the core runs in Polled mode and this
    /// component drains the event queues in Update).
    /// </summary>
    [AddComponentMenu("NetSync/NetSync Manager")]
    public class NetSyncManager : MonoBehaviour {
        public enum NetRole {
            Host,   // server + local client
            Server,
            Client
        }

        [Serializable]
        public class ChannelDefinition {
            public byte id;
            public TransportType transport = TransportType.Tcp;
            public ReliabilityMode reliability = ReliabilityMode.Unreliable;
            public bool compression;
            public bool encryption;
        }

        [Header("Role")]
        public NetRole role = NetRole.Host;
        public bool startOnAwake = true;

        [Header("Connection")]
        [Tooltip("Server address for clients. Leave empty to discover a server on the LAN.")]
        public string address = "";
        [Tooltip("Port. 0 = let the OS pick one (clients then find the server via LAN discovery).")]
        public ushort port = 7777;

        [Header("Channels — client and server must use identical tables")]
        public List<ChannelDefinition> channels = new List<ChannelDefinition> {
            new ChannelDefinition { id = 0, transport = TransportType.Tcp },
            new ChannelDefinition { id = 1, transport = TransportType.Udp, reliability = ReliabilityMode.UnreliableSequenced },
            new ChannelDefinition { id = 2, transport = TransportType.Udp, reliability = ReliabilityMode.ReliableOrdered },
        };

        [Header("Encryption (for channels with the encryption flag)")]
        public KeyExchangeMode keyExchange = KeyExchangeMode.None;
        [Tooltip("Passphrase for PresharedKey mode; hashed to a 32-byte key. Must match on both sides.")]
        public string presharedKeyPassphrase = "";

        [Header("LAN discovery")]
        public bool lanDiscovery = true;
        [Tooltip("Discovery identity. Empty = Application.productName.")]
        public string appId = "";

        [Header("Timeouts")]
        public int pingIntervalMs = 1000;
        public int pingTimeoutMs = 5000;
        public int connectTimeoutMs = 10000;

        public NetClient Client { get; private set; }
        public NetServer Server { get; private set; }
        public bool IsServer => Server != null && Server.IsRunning;
        public bool IsClient => Client != null && Client.IsConnected;
        public bool IsHost => IsServer && IsClient;
        public int ServerPort => Server?.Port ?? 0;

        // Client-side events (main thread).
        public event Action Connected;
        public event Action<DisconnectReason> Disconnected;
        public event Action<byte, byte[]> DataReceived;

        // Server-side events (main thread).
        public event Action<NetConnection> ConnectionOpened;
        public event Action<NetConnection, DisconnectReason> ConnectionClosed;
        public event Action<NetConnection, byte, byte[]> ServerDataReceived;

        private LanServerBroadcaster _broadcaster;
        private LanServerListener _listener;

        private void Awake() {
            if (startOnAwake) {
                StartNetwork();
            }
        }

        /// <summary>Starts networking according to <see cref="role"/>.</summary>
        public void StartNetwork() {
            switch (role) {
                case NetRole.Server:
                    _ = StartServerAsync();
                    break;
                case NetRole.Client:
                    _ = StartClientAsync();
                    break;
                case NetRole.Host:
                    _ = StartHostAsync();
                    break;
            }
        }

        public async Task StartServerAsync() {
            if (Server != null) {
                Debug.LogWarning("[NetSync] Server already started");
                return;
            }
            Server = new NetServer(BuildConfig());
            Server.ConnectionOpened += connection => ConnectionOpened?.Invoke(connection);
            Server.ConnectionClosed += (connection, reason) => ConnectionClosed?.Invoke(connection, reason);
            Server.DataReceived += (connection, channel, data) => ServerDataReceived?.Invoke(connection, channel, data);

            try {
                int boundPort = await Server.StartAsync(port);
                Debug.Log($"[NetSync] Server listening on port {boundPort}");
                if (lanDiscovery) {
                    _broadcaster = new LanServerBroadcaster(GetAppId(), boundPort, logger: UnityNetLogger.Instance);
                    _broadcaster.Start();
                }
            }
            catch (Exception ex) {
                Debug.LogError($"[NetSync] Failed to start server: {ex}");
                Server?.Dispose();
                Server = null;
            }
        }

        public async Task StartClientAsync() {
            if (Client != null) {
                Debug.LogWarning("[NetSync] Client already started");
                return;
            }
            Client = new NetClient(BuildConfig());
            Client.Connected += () => Connected?.Invoke();
            Client.Disconnected += reason => Disconnected?.Invoke(reason);
            Client.DataReceived += (channel, data) => DataReceived?.Invoke(channel, data);

            if (!string.IsNullOrEmpty(address) && port > 0) {
                await ConnectClientAsync(address, port);
            }
            else if (lanDiscovery) {
                Debug.Log("[NetSync] Searching for a server on the LAN…");
                _listener = new LanServerListener(GetAppId(), logger: UnityNetLogger.Instance);
                _listener.ServerFound += OnServerFound;
                _listener.Start();
            }
            else {
                Debug.LogError("[NetSync] Client needs an address:port or LAN discovery enabled");
            }
        }

        public async Task StartHostAsync() {
            await StartServerAsync();
            if (Server != null) {
                address = "127.0.0.1";
                port = (ushort)Server.Port;
                await StartClientAsync();
            }
        }

        // Fires on a background thread: touch no Unity APIs besides the thread-safe Debug.
        private void OnServerFound(IPEndPoint endpoint) {
            var listener = _listener;
            _listener = null;
            listener?.Dispose();
            _ = ConnectClientAsync(endpoint.Address.ToString(), endpoint.Port);
        }

        private async Task ConnectClientAsync(string host, int serverPort) {
            try {
                await Client.ConnectAsync(host, serverPort);
                Debug.Log($"[NetSync] Connected to {host}:{serverPort} (id={Client.ConnectionId})");
            }
            catch (Exception ex) {
                Debug.LogError($"[NetSync] Failed to connect to {host}:{serverPort}: {ex.Message}");
            }
        }

        /// <summary>Client → server.</summary>
        public void Send(byte channel, byte[] data) {
            _ = Client?.SendAsync(channel, data);
        }

        /// <summary>Server → one client.</summary>
        public void Send(NetConnection connection, byte channel, byte[] data) {
            _ = connection.SendAsync(channel, data);
        }

        /// <summary>Server → every client.</summary>
        public void Broadcast(byte channel, byte[] data) {
            _ = Server?.BroadcastAsync(channel, data);
        }

        private void Update() {
            // Polled delivery: all events surface here, on the main thread.
            Client?.PollEvents();
            Server?.PollEvents();
        }

        public void StopNetwork() {
            _listener?.Dispose();
            _listener = null;
            _broadcaster?.Dispose();
            _broadcaster = null;
            Client?.Dispose();
            Client = null;
            Server?.Dispose();
            Server = null;
        }

        private void OnDestroy() => StopNetwork();

        private NetConfig BuildConfig() {
            var config = new NetConfig {
                EventDelivery = EventDelivery.Polled,
                PingIntervalMs = pingIntervalMs,
                PingTimeoutMs = pingTimeoutMs,
                ConnectTimeoutMs = connectTimeoutMs,
                Logger = UnityNetLogger.Instance
            };
            foreach (var channel in channels) {
                config.Channels[channel.id] = new ChannelConfig(channel.transport, channel.reliability,
                                                                channel.compression, channel.encryption);
            }
            config.Encryption.Mode = keyExchange;
            if (keyExchange == KeyExchangeMode.PresharedKey) {
                using var sha = SHA256.Create();
                config.Encryption.PresharedKey = sha.ComputeHash(Encoding.UTF8.GetBytes(presharedKeyPassphrase));
            }
            return config;
        }

        private string GetAppId() {
            return string.IsNullOrEmpty(appId) ? Application.productName : appId;
        }
    }
}
