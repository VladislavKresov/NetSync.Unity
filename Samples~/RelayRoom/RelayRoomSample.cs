using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using NetSync;
using NetSync.Relay;
using NetSync.Unity;
using UnityEngine;

namespace NetSync.Unity.Samples {
    /// <summary>
    /// A player-hosted game behind NAT: the host opens a room on a relay and shares its code,
    /// players join by the code. UDP tries a direct, hole-punched path; everything stays end-to-end
    /// encrypted — the relay only forwards what it can't read. For a local try-out the host can run
    /// the relay itself; in production it runs on a public server (NetRelayServer, a few lines of .NET).
    /// Setup: empty scene, one GameObject with this component.
    /// </summary>
    [RequireComponent(typeof(NetSyncManager))]
    public class RelayRoomSample : MonoBehaviour {
        private const byte ChatChannel = 0;
        private const byte StateChannel = 1;

        private NetSyncManager _net;
        private NetRelayServer _localRelay;
        private readonly List<string> _lines = new List<string>();
        private string _relayAddress = "127.0.0.1";
        private string _relayPort = "7000";
        private bool _runRelayHere = true;
        private string _code = "";
        private string _input = "";
        private bool _started;

        private void Awake() {
            _net = GetComponent<NetSyncManager>();
            _net.startAutomatically = false;
            _net.lanDiscovery = false;
            _net.useRelay = true;
            _net.port = 0; // the host's own port doesn't matter: players come through the relay
            _net.keyExchange = KeyExchangeMode.PresharedKey;
            _net.presharedKeyPassphrase = "known to the players, not to the relay";

            _net.Connected += () => _lines.Add($"<in the room, {_net.Client.CipherSuite}>");
            _net.Disconnected += reason => _lines.Add($"<left the room: {reason}>");
            _net.ConnectFailed += error => _lines.Add($"<could not join: {error.Message}>");
            _net.DataReceived += (channel, data) => {
                if (channel == ChatChannel) {
                    _lines.Add(Encoding.UTF8.GetString(data));
                }
            };
            _net.ServerDataReceived += (connection, channel, data) => {
                if (channel == ChatChannel) {
                    _net.Broadcast(ChatChannel, data);
                }
            };
        }

        private async Task HostAsync() {
            ushort relayPort = ushort.Parse(_relayPort);
            if (_runRelayHere) {
                _localRelay = new NetRelayServer(UnityNetLogger.Instance);
                await _localRelay.StartAsync(relayPort);
            }
            _net.relayAddress = _relayAddress;
            _net.relayPort = relayPort;
            _net.role = NetSyncManager.NetRole.Host;
            _started = true;
            await _net.StartHostAsync();
        }

        private void Join() {
            _net.relayAddress = _relayAddress;
            _net.relayPort = ushort.Parse(_relayPort);
            _net.roomCode = _code.Trim().ToUpperInvariant();
            _net.role = NetSyncManager.NetRole.Client;
            _started = true;
            _ = _net.StartClientAsync();
        }

        private void OnGUI() {
            GUILayout.BeginArea(new Rect(10, 10, 420, 340), GUI.skin.box);
            if (!_started) {
                GUILayout.Label("Relay address and port");
                GUILayout.BeginHorizontal();
                _relayAddress = GUILayout.TextField(_relayAddress);
                _relayPort = GUILayout.TextField(_relayPort, GUILayout.Width(70));
                GUILayout.EndHorizontal();
                _runRelayHere = GUILayout.Toggle(_runRelayHere, "Run the relay in this instance (for testing)");
                if (GUILayout.Button("Host a room")) {
                    _ = HostAsync();
                }
                GUILayout.Space(10);
                _code = GUILayout.TextField(_code);
                if (GUILayout.Button("Join by code")) {
                    Join();
                }
                GUILayout.EndArea();
                return;
            }

            if (_net.RoomCode != null) {
                GUILayout.Label($"Room code: {_net.RoomCode}");
            }
            if (_localRelay != null) {
                GUILayout.Label($"Relayed: {_localRelay.RelayedTcpBytes} B TCP, {_localRelay.RelayedUdpBytes} B UDP (small UDP = the direct path works)");
            }
            foreach (var line in _lines) {
                GUILayout.Label(line);
            }
            GUILayout.FlexibleSpace();
            GUILayout.BeginHorizontal();
            _input = GUILayout.TextField(_input, GUILayout.ExpandWidth(true));
            if (GUILayout.Button("Send", GUILayout.Width(60)) && _input.Length > 0 && _net.IsClient) {
                _net.Send(ChatChannel, Encoding.UTF8.GetBytes(_input));
                _input = "";
            }
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        private void Update() {
            if (_net.IsClient && Time.frameCount % 10 == 0) {
                _net.Send(StateChannel, new byte[32]); // UDP traffic: watch it leave the relay once the punch succeeds
            }
        }

        private void OnDestroy() => _localRelay?.Dispose();
    }
}
