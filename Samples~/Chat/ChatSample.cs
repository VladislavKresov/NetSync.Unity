using System.Collections.Generic;
using NetSync.Messaging;
using NetSync.Serialization;
using NetSync.Unity;
using UnityEngine;

namespace NetSync.Unity.Samples {
    /// <summary>One line of chat: a typed message, no manual byte handling. A struct costs no allocation per message.</summary>
    public struct ChatLine : INetMessage {
        public string Author;
        public string Text;

        public void Write(NetWriter writer) => writer.WriteString(Author).WriteString(Text);

        public void Read(NetReader reader) {
            Author = reader.ReadString();
            Text = reader.ReadString();
        }
    }

    /// <summary>
    /// LAN chat with typed messages over the reliable TCP channel (0). Setup: empty scene,
    /// one GameObject with this component. Build and run two instances: one hosts, the other
    /// joins — the client finds the host via LAN discovery, no addresses needed.
    /// </summary>
    [RequireComponent(typeof(NetSyncManager))]
    public class ChatSample : MonoBehaviour {
        private const byte ChatChannel = 0;

        private NetSyncManager _net;
        private readonly List<string> _lines = new List<string>();
        private string _name = "Player";
        private string _input = "";
        private Vector2 _scroll;
        private bool _started;

        private void Awake() {
            _net = GetComponent<NetSyncManager>();
            _net.startAutomatically = false; // the buttons below choose the role
            _net.Messages = new NetMessageRegistry().Register<ChatLine>(1);
            _net.messageChannels.Add(ChatChannel);

            _net.Connected += () => AddLine("<connected>");
            _net.Disconnected += reason => AddLine($"<disconnected: {reason}>");
            _net.ConnectFailed += error => AddLine($"<could not connect: {error.Message}>");
            _net.On<ChatLine>(line => AddLine($"{line.Author}: {line.Text}"));
            // The server relays every line to everyone, the sender included.
            _net.OnServer<ChatLine>((connection, line) => _net.BroadcastTyped(ChatChannel, line));
        }

        private void AddLine(string line) {
            _lines.Add(line);
            if (_lines.Count > 100) {
                _lines.RemoveAt(0);
            }
            _scroll.y = float.MaxValue;
        }

        private void Begin(NetSyncManager.NetRole role) {
            _net.role = role;
            _started = true;
            _net.StartNetwork();
            AddLine(role == NetSyncManager.NetRole.Host ? "<hosting>" : "<searching for a host on the LAN…>");
        }

        private void OnGUI() {
            GUILayout.BeginArea(new Rect(10, 10, 420, 340), GUI.skin.box);
            if (!_started) {
                GUILayout.Label("NetSync chat");
                _name = GUILayout.TextField(_name);
                if (GUILayout.Button("Host")) {
                    Begin(NetSyncManager.NetRole.Host);
                }
                if (GUILayout.Button("Join (LAN)")) {
                    Begin(NetSyncManager.NetRole.Client);
                }
                GUILayout.EndArea();
                return;
            }

            string status = _net.IsHost ? "host" : _net.IsClient ? "client" : "connecting…";
            GUILayout.Label($"{status}, ping {_net.Client?.PingMs ?? -1} ms");

            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(240));
            foreach (var line in _lines) {
                GUILayout.Label(line);
            }
            GUILayout.EndScrollView();

            GUILayout.BeginHorizontal();
            bool enter = Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Return;
            _input = GUILayout.TextField(_input, GUILayout.ExpandWidth(true));
            bool send = GUILayout.Button("Send", GUILayout.Width(60)) || enter;
            GUILayout.EndHorizontal();
            GUILayout.EndArea();

            if (send && !string.IsNullOrWhiteSpace(_input) && _net.IsClient) {
                _net.SendTyped(ChatChannel, new ChatLine { Author = _name, Text = _input });
                _input = "";
            }
        }
    }
}
