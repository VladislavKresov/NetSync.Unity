using System.Collections.Generic;
using System.Text;
using NetSync.Unity;
using UnityEngine;

namespace NetSync.Unity.Samples {
    /// <summary>
    /// Minimal LAN chat over the reliable TCP channel (0). Setup: empty scene,
    /// one GameObject with NetSyncManager + this component. Build & run two
    /// instances: one as Host, one as Client — the client finds the host via
    /// LAN discovery, no addresses needed.
    /// </summary>
    [RequireComponent(typeof(NetSyncManager))]
    public class ChatSample : MonoBehaviour {
        private const byte ChatChannel = 0;

        private NetSyncManager _net;
        private readonly List<string> _messages = new List<string>();
        private string _input = "";
        private Vector2 _scroll;

        private void Awake() {
            _net = GetComponent<NetSyncManager>();
            _net.Connected += () => AddMessage("<connected>");
            _net.Disconnected += reason => AddMessage($"<disconnected: {reason}>");
            _net.DataReceived += (channel, data) => {
                if (channel == ChatChannel) {
                    AddMessage(Encoding.UTF8.GetString(data));
                }
            };
            // Server relays chat to everyone, including the sender's own client.
            _net.ServerDataReceived += (connection, channel, data) => {
                if (channel == ChatChannel) {
                    _net.Broadcast(ChatChannel, data);
                }
            };
        }

        private void AddMessage(string message) {
            _messages.Add(message);
            if (_messages.Count > 100) {
                _messages.RemoveAt(0);
            }
            _scroll.y = float.MaxValue;
        }

        private void OnGUI() {
            GUILayout.BeginArea(new Rect(10, 10, 400, 320), GUI.skin.box);
            string status = _net.IsHost ? "host" : _net.IsServer ? "server" : _net.IsClient ? "client" : "connecting…";
            GUILayout.Label($"NetSync chat — {status}, ping {_net.Client?.PingMs ?? -1} ms");

            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(220));
            foreach (var message in _messages) {
                GUILayout.Label(message);
            }
            GUILayout.EndScrollView();

            GUILayout.BeginHorizontal();
            _input = GUILayout.TextField(_input, GUILayout.ExpandWidth(true));
            bool send = GUILayout.Button("Send", GUILayout.Width(60)) ||
                        (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Return);
            GUILayout.EndHorizontal();
            GUILayout.EndArea();

            if (send && !string.IsNullOrWhiteSpace(_input) && _net.IsClient) {
                _net.Send(ChatChannel, Encoding.UTF8.GetBytes(_input));
                _input = "";
            }
        }
    }
}
