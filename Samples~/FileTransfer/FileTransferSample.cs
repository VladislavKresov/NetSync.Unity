using System;
using System.IO;
using System.Threading.Tasks;
using NetSync;
using NetSync.Peers;
using NetSync.Streams;
using NetSync.Unity;
using UnityEngine;

namespace NetSync.Unity.Samples {
    /// <summary>
    /// A file of any size next to real-time traffic: the client streams a file on a reliable
    /// channel with Low priority — it takes the spare bandwidth and yields to everything else —
    /// while the position channel keeps measuring its round trip. Memory stays at a few MB.
    /// Setup: empty scene, one GameObject with this component.
    /// </summary>
    [RequireComponent(typeof(NetSyncManager))]
    public class FileTransferSample : MonoBehaviour {
        private const byte PingChannel = 1;
        private const byte FileChannel = 3;
        private const int TestFileBytes = 32 * 1024 * 1024;

        private NetSyncManager _net;
        private string _folder;
        private string _status = "";
        private NetStreamInfo _receiving;
        private bool _sending;
        private float _pingMs = -1;
        private float _nextPing;

        private void Awake() {
            _net = GetComponent<NetSyncManager>();
            _net.startAutomatically = false;
            _net.channels.Add(new NetSyncManager.ChannelDefinition {
                id = FileChannel,
                transport = TransportType.Udp,
                reliability = ReliabilityMode.ReliableOrdered,
                priority = ChannelPriority.Low
            });
            _folder = Path.Combine(Application.temporaryCachePath, "Received");

            _net.ConnectionOpened += connection => connection.Tag = CreateReceiver();
            _net.ConnectionClosed += (connection, reason) => (connection.Tag as NetStreamReceiver)?.Dispose();
            _net.ServerMessageReceived += (connection, channel, data) => {
                if (channel == FileChannel) {
                    ((NetStreamReceiver)connection.Tag).HandleMessage(data); // written straight from the network buffer
                }
                else if (channel == PingChannel) {
                    _net.Send(connection, PingChannel, data); // echo
                }
            };
            _net.MessageReceived += (channel, data) => {
                if (channel == PingChannel) {
                    _pingMs = (Time.realtimeSinceStartup - BitConverter.ToSingle(data.Span)) * 1000f;
                }
            };
        }

        private NetStreamReceiver CreateReceiver() {
            Directory.CreateDirectory(_folder);
            // The name comes from the remote: never use it as a path as it is.
            var receiver = new NetStreamReceiver(info => File.Create(Path.Combine(_folder, Path.GetFileName(info.Name))));
            receiver.StreamStarted += info => _receiving = info;
            receiver.StreamCompleted += info => { _receiving = null; _status = $"Saved {info.Name} to {_folder}"; };
            receiver.StreamFailed += (info, reason) => { _receiving = null; _status = $"{info.Name} failed: {reason}"; };
            return receiver;
        }

        private async Task SendTestFileAsync() {
            _sending = true;
            string path = Path.Combine(Application.temporaryCachePath, "netsync-test.bin");
            try {
                _status = "Preparing the file…";
                await Task.Run(() => {
                    var bytes = new byte[TestFileBytes];
                    new System.Random().NextBytes(bytes);
                    File.WriteAllBytes(path, bytes);
                });
                _status = "Sending…";
                float started = Time.realtimeSinceStartup;
                using (var file = File.OpenRead(path)) {
                    await _net.Client.SendStreamAsync(FileChannel, file, "netsync-test.bin");
                }
                _status = $"Sent {TestFileBytes / 1048576} MB in {Time.realtimeSinceStartup - started:F1} s";
            }
            catch (Exception ex) {
                _status = $"Failed: {ex.Message}";
            }
            finally {
                _sending = false;
                File.Delete(path);
            }
        }

        private void Update() {
            if (_net.IsClient && Time.realtimeSinceStartup >= _nextPing) {
                _nextPing = Time.realtimeSinceStartup + 0.05f;
                _net.Send(PingChannel, BitConverter.GetBytes(Time.realtimeSinceStartup));
            }
        }

        private void OnGUI() {
            GUILayout.BeginArea(new Rect(10, 10, 460, 220), GUI.skin.box);
            if (!_net.IsServer && !_net.IsClient) {
                if (GUILayout.Button("Receive files (server)")) {
                    Begin(NetSyncManager.NetRole.Server);
                }
                if (GUILayout.Button("Send files (client, LAN)")) {
                    Begin(NetSyncManager.NetRole.Client);
                }
            }
            if (_net.IsClient) {
                GUILayout.Label($"Round trip on the real-time channel: {_pingMs:F1} ms");
                GUI.enabled = !_sending;
                if (GUILayout.Button($"Send a {TestFileBytes / 1048576} MB test file")) {
                    _ = SendTestFileAsync();
                }
                GUI.enabled = true;
            }
            var receiving = _receiving;
            if (receiving != null) {
                GUILayout.Label($"Receiving {receiving.Name}: {receiving.BytesReceived / 1048576f:F1} of {receiving.Length / 1048576f:F1} MB");
            }
            GUILayout.Label(_status);
            GUILayout.EndArea();
        }

        private void Begin(NetSyncManager.NetRole role) {
            _net.role = role;
            _net.StartNetwork();
            _status = role == NetSyncManager.NetRole.Server ? "Waiting for a client" : "Searching for a server…";
        }
    }
}
