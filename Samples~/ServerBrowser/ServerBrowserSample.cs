using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NetSync.Discovery;
using NetSync.Unity;
using UnityEngine;

namespace NetSync.Unity.Samples {
    /// <summary>
    /// A server list: hosts announce themselves on the LAN with a name, clients list them and
    /// ask each one for its player count and ping with a connectionless query — no connection
    /// until the player presses Join. Setup: empty scene, one GameObject with this component.
    /// </summary>
    [RequireComponent(typeof(NetSyncManager))]
    public class ServerBrowserSample : MonoBehaviour {
        private sealed class Entry {
            public IPEndPoint EndPoint;
            public string Name;
            public string Details = "querying…";
            public bool Querying;
        }

        private const float QueryIntervalSeconds = 2f;

        private NetSyncManager _net;
        private readonly Dictionary<string, Entry> _servers = new Dictionary<string, Entry>();
        private string _serverName = "Anna's game";
        private string _status = "";
        private float _nextQuery;
        private int _players; // read by the query handler on a network thread

        private void Awake() {
            _net = GetComponent<NetSyncManager>();
            _net.startAutomatically = false;
            _net.autoJoinDiscovered = false; // list servers instead of joining the first one
            _net.ServerDiscovered += found => {
                string key = found.EndPoint.ToString();
                if (!_servers.ContainsKey(key)) {
                    _servers[key] = new Entry { EndPoint = found.EndPoint, Name = Encoding.UTF8.GetString(found.Payload) };
                }
            };
            _net.ConnectionOpened += connection => Interlocked.Increment(ref _players);
            _net.ConnectionClosed += (connection, reason) => Interlocked.Decrement(ref _players);
            _net.Connected += () => _status = "Joined";
            _net.ConnectFailed += error => _status = error.Message;
            // Runs on a network thread: no Unity APIs. The reply can't be larger than the query (no amplification).
            _net.QueryHandler = (from, request) => Encoding.UTF8.GetBytes($"{Volatile.Read(ref _players)} players");
        }

        private void Host() {
            _net.DiscoveryPayload = Encoding.UTF8.GetBytes(_serverName);
            _net.role = NetSyncManager.NetRole.Server;
            _net.StartNetwork();
            _status = "Hosting";
        }

        private void Update() {
            if (Time.time < _nextQuery || _net.IsClient || _net.IsServer) {
                return;
            }
            _nextQuery = Time.time + QueryIntervalSeconds;
            foreach (var entry in _servers.Values) {
                if (!entry.Querying) {
                    _ = QueryAsync(entry);
                }
            }
        }

        private static async Task QueryAsync(Entry entry) {
            entry.Querying = true;
            try {
                var result = await NetQuery.QueryAsync(entry.EndPoint, new byte[0], maxResponseSize: 64, timeoutMs: 500, attempts: 2);
                entry.Details = $"{Encoding.UTF8.GetString(result.Response)}, {result.RoundTripMs:F0} ms";
            }
            catch (System.TimeoutException) {
                entry.Details = "not answering";
            }
            finally {
                entry.Querying = false;
            }
        }

        private void OnGUI() {
            GUILayout.BeginArea(new Rect(10, 10, 420, 320), GUI.skin.box);
            if (_net.IsServer || _net.IsClient) {
                GUILayout.Label(_status);
                if (_net.IsServer) {
                    GUILayout.Label($"'{_serverName}': {Volatile.Read(ref _players)} player(s) connected");
                }
                else {
                    GUILayout.Label($"Ping {_net.Client.PingMs} ms");
                }
                GUILayout.EndArea();
                return;
            }

            GUILayout.BeginHorizontal();
            _serverName = GUILayout.TextField(_serverName, GUILayout.ExpandWidth(true));
            if (GUILayout.Button("Host", GUILayout.Width(80))) {
                Host();
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(10);
            if (GUILayout.Button("Search the LAN")) {
                _servers.Clear();
                _net.StartLanSearch();
                _status = "Searching…";
            }
            foreach (var entry in _servers.Values) {
                GUILayout.BeginHorizontal();
                GUILayout.Label($"{entry.Name} — {entry.Details}");
                if (GUILayout.Button("Join", GUILayout.Width(60))) {
                    _net.StopLanSearch();
                    _net.role = NetSyncManager.NetRole.Client;
                    _ = _net.ConnectToAsync(entry.EndPoint.Address.ToString(), entry.EndPoint.Port);
                    _status = "Joining…";
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.Label(_status);
            GUILayout.EndArea();
        }
    }
}
