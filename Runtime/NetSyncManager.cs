using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using NetSync.Discovery;
using NetSync.Messaging;
using NetSync.Peers;
using NetSync.Relay;
using NetSync.Serialization;
using NetSync.Transports;
using NetSync.Transports.Simulation;
using UnityEngine;

namespace NetSync.Unity {
    /// <summary>
    /// Scene-facing facade over the NetSync core: drop it on a GameObject, pick a role,
    /// press play.
    ///
    /// All events fire on the Unity main thread (the core runs in Polled mode and this
    /// component drains the event queues in Update). Configure from code in Awake or
    /// OnEnable: networking starts in Start, after every Awake.
    /// </summary>
    [AddComponentMenu("NetSync/NetSync Manager")]
    [DefaultExecutionOrder(-100)]
    public class NetSyncManager : MonoBehaviour {
        public enum NetRole {
            Host,   // server + local client
            Server,
            Client
        }

        public enum PauseBehavior {
            /// <summary>Keep the connection; the OS may kill the sockets while the app is suspended.</summary>
            KeepRunning,
            /// <summary>On mobile: disconnect when the app is suspended, start again when it resumes.</summary>
            DisconnectAndResume
        }

        [Serializable]
        public class ChannelDefinition {
            public byte id;
            public TransportType transport = TransportType.Tcp;
            public ReliabilityMode reliability = ReliabilityMode.Unreliable;
            public ChannelPriority priority = ChannelPriority.Normal;
            public bool compression;
            public bool encryption;
        }

        /// <summary>The most recently awakened manager (for scripts on other objects).</summary>
        public static NetSyncManager Instance { get; private set; }

        [Header("Role")]
        public NetRole role = NetRole.Host;
        [Tooltip("Start networking in Start() (after every Awake, so other components can configure the manager first).")]
        public bool startAutomatically = true;
        [Tooltip("Keep running without focus: events are only delivered while Update runs.")]
        public bool runInBackground = true;

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

        [Tooltip("Channels that carry typed messages (Messages registry). Empty = every channel.")]
        public List<byte> messageChannels = new List<byte>();

        [Header("Security")]
        [Tooltip("Any mode other than None authenticates every packet; channels with the encryption flag are also encrypted.")]
        public KeyExchangeMode keyExchange = KeyExchangeMode.None;
        [Tooltip("Passphrase for PresharedKey mode; hashed to a 32-byte key. Must match on both sides.")]
        public string presharedKeyPassphrase = "";
        [Tooltip("Clients, Ecdh mode: base64 public keys of trusted servers (NetIdentity.PublicKey). Empty = any server.")]
        public List<string> trustedServerKeys = new List<string>();
        [Tooltip("Server: open connections beyond this are refused. 0 = unlimited.")]
        public int maxConnections;

        [Header("LAN discovery")]
        public bool lanDiscovery = true;
        [Tooltip("Discovery identity. Empty = Application.productName.")]
        public string appId = "";
        [Tooltip("Client without an address: join the first server found. Off = only raise ServerDiscovered.")]
        public bool autoJoinDiscovered = true;

        [Header("Relay (play across NATs)")]
        public bool useRelay;
        public string relayAddress = "";
        public ushort relayPort = 7000;
        [Tooltip("Host: empty = generate a code (see RoomCode). Client: the code to join.")]
        public string roomCode = "";
        [Tooltip("Try a direct, hole-punched UDP path; off = everything through the relay.")]
        public bool allowDirectPath = true;

        [Header("Resilience")]
        [Tooltip("Reconnect automatically after a lost connection (not after a kick or StopNetwork).")]
        public bool autoReconnect = true;
        public int reconnectMaxAttempts = 10;
        public PauseBehavior pauseBehavior = PauseBehavior.DisconnectAndResume;

        [Header("Timeouts")]
        public int pingIntervalMs = 1000;
        public int pingTimeoutMs = 5000;
        public int connectTimeoutMs = 10000;

        [Header("Network simulation (Editor and development builds only)")]
        public bool simulateNetwork;
        public int simulatedLatencyMs = 80;
        public int simulatedJitterMs = 20;
        [Range(0f, 1f)] public float simulatedLoss = 0.02f;
        [Range(0f, 1f)] public float simulatedDuplicates;

        public NetClient Client { get; private set; }
        public NetServer Server { get; private set; }
        /// <summary>True once every listener (relay room included) is up.</summary>
        public bool IsServer => Server != null && _serverReady;
        public bool IsClient => Client != null && Client.IsConnected;
        public bool IsHost => IsServer && IsClient;
        public int ServerPort => Server?.Port ?? 0;

        /// <summary>The relay room this host opened or this client joins; null when not on a relay.</summary>
        public string RoomCode { get; private set; }

        /// <summary>
        /// A timeline shared by server and clients, in ms: the server's clock, or the client's
        /// estimate of it. NaN until the first sync.
        /// </summary>
        public double ServerTimeMs => IsServer ? Server.TimeMs : Client?.ServerTimeMs ?? double.NaN;

        /// <summary>Typed messages: set before networking starts; creates the messengers below.</summary>
        public NetMessageRegistry Messages { get; set; }
        public NetClientMessenger ClientMessenger { get; private set; }
        public NetServerMessenger ServerMessenger { get; private set; }

        /// <summary>Sent with every connect (login token, version…), encrypted when a key exchange is set.</summary>
        public byte[] ConnectData { get; set; }

        /// <summary>Server, Ecdh mode: signs handshakes. Set it from code — keep the private key out of the scene.</summary>
        public NetIdentity ServerIdentity { get; set; }

        /// <summary>Server: decides who may join, from their connect data. Null = everyone.</summary>
        public ConnectionApprover ConnectionApprover {
            get => _approver;
            set {
                _approver = value;
                if (Server != null) {
                    Server.ConnectionApprover = value;
                }
            }
        }

        /// <summary>
        /// Server: answers NetQuery requests (server browsers). Runs on a network thread for
        /// anyone on the internet — keep it fast and away from Unity APIs.
        /// </summary>
        public Func<IPEndPoint, ReadOnlyMemory<byte>, byte[]> QueryHandler {
            get => _queryHandler;
            set {
                _queryHandler = value;
                if (Server != null) {
                    Server.QueryHandler = value;
                }
            }
        }

        /// <summary>Server: description sent with LAN announcements (≤ 1 KB). Change it any time.</summary>
        public byte[] DiscoveryPayload {
            get => _discoveryPayload;
            set {
                _discoveryPayload = value;
                if (_broadcaster != null) {
                    _broadcaster.Payload = value;
                }
            }
        }

        // Client-side events (main thread).
        public event Action Connected;
        public event Action<DisconnectReason> Disconnected;
        /// <summary>A connect attempt failed; a <see cref="ConnectionRejectedException"/> carries the reason.</summary>
        public event Action<Exception> ConnectFailed;
        /// <summary>Auto-reconnect is about to make attempt N.</summary>
        public event Action<int> Reconnecting;
        /// <summary>Zero-copy: the data is valid only until the handler returns.</summary>
        public event NetDataHandler MessageReceived;
        /// <summary>Each message as a new array; prefer <see cref="MessageReceived"/> every frame.</summary>
        public event Action<byte, byte[]> DataReceived;
        /// <summary>A server answered on the LAN (see <see cref="StartLanSearch"/>).</summary>
        public event Action<DiscoveredServer> ServerDiscovered;

        // Server-side events (main thread).
        /// <summary>Every listener is up; <see cref="ServerPort"/> and <see cref="RoomCode"/> are final.</summary>
        public event Action ServerStarted;
        public event Action<NetConnection> ConnectionOpened;
        public event Action<NetConnection, DisconnectReason> ConnectionClosed;
        /// <summary>Zero-copy: the data is valid only until the handler returns.</summary>
        public event NetConnectionDataHandler ServerMessageReceived;
        /// <summary>Each message as a new array; prefer <see cref="ServerMessageReceived"/> every frame.</summary>
        public event Action<NetConnection, byte, byte[]> ServerDataReceived;

        private readonly ConcurrentQueue<Action> _mainThread = new ConcurrentQueue<Action>();
        private ConnectionApprover _approver;
        private Func<IPEndPoint, ReadOnlyMemory<byte>, byte[]> _queryHandler;
        private byte[] _discoveryPayload;
        private LanServerBroadcaster _broadcaster;
        private LanServerListener _listener;
        private bool _resumeAfterPause;
        private bool _serverReady;
        private readonly List<Action<NetClientMessenger>> _clientHandlers = new List<Action<NetClientMessenger>>();
        private readonly List<Action<NetServerMessenger>> _serverHandlers = new List<Action<NetServerMessenger>>();

        private void Awake() {
            Instance = this;
            if (runInBackground) {
                Application.runInBackground = true;
            }
        }

        private void Start() {
            if (startAutomatically) {
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
            NetServer server = null;
            try {
                server = new NetServer(BuildConfig(forClient: false));
                Server = server;
                server.ConnectionApprover = _approver;
                server.QueryHandler = _queryHandler;
                server.ConnectionOpened += connection => ConnectionOpened?.Invoke(connection);
                server.ConnectionClosed += (connection, reason) => ConnectionClosed?.Invoke(connection, reason);
                server.MessageReceived += OnServerMessage;
                if (Messages != null) {
                    ServerMessenger = new NetServerMessenger(server, Messages, messageChannels.ToArray());
                    ServerMessenger.MalformedMessage += (connection, error) => Debug.LogWarning($"[NetSync] Malformed message from {connection}: {error.Message}");
                    _serverHandlers.ForEach(add => add(ServerMessenger));
                }
                if (useRelay) {
                    await AddRelayRoomAsync(server);
                }
                int boundPort = await server.StartAsync(port);
                if (Server != server) {
                    return; // stopped while starting
                }
                Debug.Log($"[NetSync] Server listening on port {boundPort}");
                if (lanDiscovery) {
                    _broadcaster = new LanServerBroadcaster(GetAppId(), boundPort, logger: UnityNetLogger.Instance);
                    if (_discoveryPayload != null) {
                        _broadcaster.Payload = _discoveryPayload;
                    }
                    _broadcaster.Start();
                }
                _serverReady = true;
                ServerStarted?.Invoke();
            }
            catch (Exception ex) {
                Debug.LogError($"[NetSync] Failed to start server: {ex.Message}");
                if (Server == server) {
                    ReleaseServer();
                }
            }
        }

        private async Task AddRelayRoomAsync(NetServer server) {
            RoomCode = string.IsNullOrEmpty(roomCode) ? RelayRoom.NewRoomCode() : roomCode;
            var room = new RelayRoom(RoomCode) {
                RelayEndPoint = await ResolveAsync(relayAddress, relayPort),
                AllowDirect = allowDirectPath
            };
            var slots = new HashSet<TransportType>();
            foreach (var channel in channels) {
                if (slots.Add(channel.transport)) {
                    server.AddListener(channel.transport, channel.transport == TransportType.Tcp ? room.Tcp : room.Udp, 0);
                }
            }
            Debug.Log($"[NetSync] Relay room: {RoomCode}");
        }

        private static async Task<IPEndPoint> ResolveAsync(string host, int hostPort) {
            if (IPAddress.TryParse(host, out var literal)) {
                return new IPEndPoint(literal, hostPort);
            }
            var addresses = await Dns.GetHostAddressesAsync(host);
            return new IPEndPoint(addresses[0], hostPort);
        }

        public async Task StartClientAsync() {
            if (Client != null) {
                Debug.LogWarning("[NetSync] Client already started");
                return;
            }
            if (useRelay) {
                if (string.IsNullOrEmpty(roomCode)) {
                    Debug.LogError("[NetSync] A relay client needs a room code");
                    return;
                }
                RoomCode = roomCode;
                await ConnectToAsync(relayAddress, relayPort);
            }
            else if (!string.IsNullOrEmpty(address) && port > 0) {
                await ConnectToAsync(address, port);
            }
            else if (lanDiscovery) {
                Debug.Log("[NetSync] Searching for a server on the LAN…");
                StartLanSearch();
            }
            else {
                Debug.LogError("[NetSync] Client needs an address:port or LAN discovery enabled");
            }
        }

        public async Task StartHostAsync() {
            await StartServerAsync();
            if (IsServer) {
                await ConnectToAsync("127.0.0.1", Server.Port); // the local player never goes through the relay
            }
        }

        /// <summary>Starts listening for LAN servers: raises <see cref="ServerDiscovered"/>, joins the first if <see cref="autoJoinDiscovered"/>.</summary>
        public void StartLanSearch() {
            if (_listener != null) {
                return;
            }
            var listener = new LanServerListener(GetAppId(), logger: UnityNetLogger.Instance);
            // Fires on a background thread: everything Unity-facing moves to Update.
            listener.ServerDiscovered += found => _mainThread.Enqueue(() => OnServerDiscovered(listener, found));
            try {
                listener.Start();
                _listener = listener;
            }
            catch (Exception ex) {
                Debug.LogError($"[NetSync] LAN search could not start: {ex.Message}");
            }
        }

        public void StopLanSearch() {
            _listener?.Dispose();
            _listener = null;
        }

        private void OnServerDiscovered(LanServerListener listener, DiscoveredServer found) {
            if (_listener != listener) {
                return; // stopped meanwhile
            }
            ServerDiscovered?.Invoke(found);
            if (autoJoinDiscovered) {
                StopLanSearch();
                _ = ConnectToAsync(found.EndPoint.Address.ToString(), found.EndPoint.Port);
            }
        }

        /// <summary>Connects this client to a specific server.</summary>
        public async Task ConnectToAsync(string host, int serverPort) {
            NetClient client = null;
            try {
                if (Client != null && Client.IsConnected) {
                    Debug.LogWarning("[NetSync] Already connected");
                    return;
                }
                client = Client ?? CreateClient();
                await client.ConnectAsync(host, serverPort, ConnectData);
                if (Client != client) {
                    return; // stopped while connecting
                }
                Debug.Log($"[NetSync] Connected to {host}:{serverPort} (id={client.ConnectionId}, {client.CipherSuite})");
            }
            catch (Exception ex) {
                if (client != null && Client != client) {
                    return; // stopped while connecting
                }
                Debug.LogWarning($"[NetSync] Failed to connect to {host}:{serverPort}: {ex.Message}");
                ReleaseClient();
                ConnectFailed?.Invoke(ex);
            }
        }

        private NetClient CreateClient() {
            var client = new NetClient(BuildConfig(forClient: true));
            Client = client;
            client.Connected += () => Connected?.Invoke();
            client.Disconnected += reason => Disconnected?.Invoke(reason);
            client.Reconnecting += attempt => Reconnecting?.Invoke(attempt);
            client.ReconnectFailed += () => Debug.LogWarning("[NetSync] Gave up reconnecting");
            client.MessageReceived += OnClientMessage;
            if (Messages != null) {
                ClientMessenger = new NetClientMessenger(client, Messages, messageChannels.ToArray());
                ClientMessenger.MalformedMessage += error => Debug.LogWarning($"[NetSync] Malformed message: {error.Message}");
                _clientHandlers.ForEach(add => add(ClientMessenger));
            }
            return client;
        }

        private void OnClientMessage(byte channel, ReadOnlyMemory<byte> data) {
            MessageReceived?.Invoke(channel, data);
            var copying = DataReceived; // the copy is only made when someone wants it
            copying?.Invoke(channel, data.ToArray());
        }

        private void OnServerMessage(NetConnection connection, byte channel, ReadOnlyMemory<byte> data) {
            ServerMessageReceived?.Invoke(connection, channel, data);
            var copying = ServerDataReceived;
            copying?.Invoke(connection, channel, data.ToArray());
        }

        /// <summary>Client → server. False when not connected. The data may be reused as soon as this returns.</summary>
        public bool Send(byte channel, ReadOnlyMemory<byte> data) {
            var client = Client;
            if (client == null || !client.IsConnected) {
                return false;
            }
            try {
                Observe(client.SendAsync(channel, data), "send");
                return true;
            }
            catch (InvalidOperationException ex) {
                Debug.LogWarning($"[NetSync] Failed to send: {ex.Message}"); // the link went down just now
                return false;
            }
        }

        public bool Send(byte channel, NetWriter writer) => Send(channel, writer.WrittenMemory);

        /// <summary>Server → one client. False when the connection is gone.</summary>
        public bool Send(NetConnection connection, byte channel, ReadOnlyMemory<byte> data) {
            try {
                Observe(connection.SendAsync(channel, data), "send");
                return true;
            }
            catch (InvalidOperationException ex) {
                Debug.LogWarning($"[NetSync] Failed to send to {connection}: {ex.Message}");
                return false;
            }
        }

        public bool Send(NetConnection connection, byte channel, NetWriter writer) => Send(connection, channel, writer.WrittenMemory);

        /// <summary>Server → every client. The array must stay unchanged until the sends are done.</summary>
        public void Broadcast(byte channel, byte[] data) {
            if (Server != null) {
                Observe(Server.BroadcastAsync(channel, data), "broadcast");
            }
        }

        public void Broadcast(byte channel, NetWriter writer) => Broadcast(channel, writer.ToArray());

        /// <summary>Client: handle a typed message. May be called before networking starts; survives restarts.</summary>
        public void On<T>(Action<T> handler) where T : INetMessage, new() {
            Action<NetClientMessenger> add = messenger => messenger.On(handler);
            _clientHandlers.Add(add);
            if (ClientMessenger != null) {
                add(ClientMessenger);
            }
        }

        /// <summary>Server: handle a typed message. May be called before networking starts; survives restarts.</summary>
        public void OnServer<T>(Action<NetConnection, T> handler) where T : INetMessage, new() {
            Action<NetServerMessenger> add = messenger => messenger.On(handler);
            _serverHandlers.Add(add);
            if (ServerMessenger != null) {
                add(ServerMessenger);
            }
        }

        /// <summary>Typed message, client → server (needs <see cref="Messages"/>).</summary>
        public void SendTyped<T>(byte channel, T message) where T : INetMessage {
            if (ClientMessenger != null && IsClient) {
                Observe(ClientMessenger.SendAsync(channel, message), "send");
            }
        }

        /// <summary>Typed message, server → one client (needs <see cref="Messages"/>).</summary>
        public void SendTyped<T>(NetConnection connection, byte channel, T message) where T : INetMessage {
            if (ServerMessenger != null) {
                Observe(ServerMessenger.SendAsync(connection, channel, message), "send");
            }
        }

        /// <summary>Typed message, server → every client (needs <see cref="Messages"/>).</summary>
        public void BroadcastTyped<T>(byte channel, T message) where T : INetMessage {
            if (ServerMessenger != null) {
                Observe(ServerMessenger.BroadcastAsync(channel, message), "broadcast");
            }
        }

        // Fire-and-forget without losing errors, and without an async machine for the usual
        // case of a send that completed synchronously.
        private static void Observe(ValueTask task, string what) {
            if (!task.IsCompletedSuccessfully) {
                ObserveSlow(task, what);
            }
        }

        private static async void ObserveSlow(ValueTask task, string what) {
            try {
                await task;
            }
            catch (Exception ex) {
                Debug.LogWarning($"[NetSync] Failed to {what}: {ex.Message}");
            }
        }

        private static async void Observe(Task task, string what) {
            try {
                await task;
            }
            catch (Exception ex) {
                Debug.LogWarning($"[NetSync] Failed to {what}: {ex.Message}");
            }
        }

        private void Update() {
            while (_mainThread.TryDequeue(out var action)) {
                action();
            }
            // Polled delivery: all events surface here, on the main thread.
            Client?.PollEvents();
            Server?.PollEvents();
        }

        private void OnApplicationPause(bool paused) {
            if (pauseBehavior != PauseBehavior.DisconnectAndResume || !Application.isMobilePlatform) {
                return;
            }
            if (paused && (Client != null || Server != null)) {
                _resumeAfterPause = true;
                StopNetwork();
            }
            else if (!paused && _resumeAfterPause) {
                _resumeAfterPause = false;
                StartNetwork();
            }
        }

        public void StopNetwork() {
            var client = Client;
            var server = Server;
            Shutdown();
            client?.PollEvents(); // Disconnected and ConnectionClosed still reach their handlers
            server?.PollEvents();
        }

        private void Shutdown() {
            StopLanSearch();
            _broadcaster?.Dispose();
            _broadcaster = null;
            ReleaseClient();
            ReleaseServer();
            RoomCode = null;
        }

        private void ReleaseClient() {
            ClientMessenger?.Dispose();
            ClientMessenger = null;
            Client?.Dispose();
            Client = null;
        }

        private void ReleaseServer() {
            _serverReady = false;
            ServerMessenger?.Dispose();
            ServerMessenger = null;
            Server?.Dispose();
            Server = null;
        }

        private void OnDestroy() {
            Shutdown();
            if (Instance == this) {
                Instance = null;
            }
        }

        private NetConfig BuildConfig(bool forClient) {
            var config = new NetConfig {
                EventDelivery = EventDelivery.Polled,
                PingIntervalMs = pingIntervalMs,
                PingTimeoutMs = pingTimeoutMs,
                ConnectTimeoutMs = connectTimeoutMs,
                MaxConnections = maxConnections,
                Logger = UnityNetLogger.Instance,
                Reconnect = forClient && autoReconnect ? new ReconnectPolicy { MaxAttempts = reconnectMaxAttempts } : null
            };
            foreach (var channel in channels) {
                config.Channels[channel.id] = new ChannelConfig(channel.transport, channel.reliability,
                                                                channel.compression, channel.encryption, channel.priority);
            }
            ApplySecurity(config, forClient);
            if (forClient && useRelay && role == NetRole.Client) {
                var room = new RelayRoom(roomCode) { AllowDirect = allowDirectPath };
                config.TransportFactories[TransportType.Tcp] = room.Tcp;
                config.TransportFactories[TransportType.Udp] = room.Udp;
            }
            // A host would otherwise impair the same local link twice.
            if (simulateNetwork && Debug.isDebugBuild && (forClient || role == NetRole.Server)) {
                config.Simulation = new NetworkSimulation {
                    LatencyMs = simulatedLatencyMs,
                    JitterMs = simulatedJitterMs,
                    LossRate = simulatedLoss,
                    DuplicateRate = simulatedDuplicates
                };
            }
            return config;
        }

        private void ApplySecurity(NetConfig config, bool forClient) {
            config.Encryption.Mode = keyExchange;
            if (keyExchange == KeyExchangeMode.PresharedKey) {
                if (string.IsNullOrEmpty(presharedKeyPassphrase)) {
                    Debug.LogWarning("[NetSync] PresharedKey mode with an empty passphrase protects nothing");
                }
                using var sha = SHA256.Create();
                config.Encryption.PresharedKey = sha.ComputeHash(Encoding.UTF8.GetBytes(presharedKeyPassphrase ?? ""));
            }
            else if (keyExchange == KeyExchangeMode.Ecdh) {
                if (!forClient) {
                    config.Encryption.ServerIdentity = ServerIdentity;
                    return;
                }
                foreach (var key in trustedServerKeys) {
                    if (!string.IsNullOrWhiteSpace(key)) {
                        config.Encryption.TrustedServerKeys.Add(Convert.FromBase64String(key.Trim()));
                    }
                }
                // A host's own client trusts its own server.
                if (role == NetRole.Host && ServerIdentity != null && config.Encryption.TrustedServerKeys.Count > 0) {
                    config.Encryption.TrustedServerKeys.Add(ServerIdentity.PublicKey);
                }
            }
        }

        private string GetAppId() {
            return string.IsNullOrEmpty(appId) ? Application.productName : appId;
        }
    }
}
