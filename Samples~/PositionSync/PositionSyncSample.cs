using System.Collections.Generic;
using NetSync.Serialization;
using NetSync.Unity;
using UnityEngine;

namespace NetSync.Unity.Samples {
    /// <summary>
    /// Server-authoritative transform sync with interpolation on a shared clock. The server
    /// stamps each snapshot with its time and sends it on channel 1 (UDP UnreliableSequenced):
    /// a lost snapshot is simply skipped, a stale one dropped. Clients render a little in the
    /// past on the server's timeline, so they always have two snapshots to interpolate between.
    /// Setup: empty scene, one GameObject with this component; it spawns its own cube.
    /// </summary>
    [RequireComponent(typeof(NetSyncManager))]
    public class PositionSyncSample : MonoBehaviour {
        private const byte StateChannel = 1;

        private struct Snapshot {
            public double TimeMs;
            public Vector3 Position;
            public Quaternion Rotation;
        }

        [SerializeField] private float orbitRadius = 3f;
        [SerializeField] private float orbitSpeed = 1.2f;
        [Tooltip("How far behind the server clients render. Two or three send intervals is plenty.")]
        [SerializeField] private double interpolationDelayMs = 100;

        private NetSyncManager _net;
        private Transform _cube;
        private readonly NetWriter _writer = new NetWriter();
        private readonly List<Snapshot> _snapshots = new List<Snapshot>();
        private bool _started;

        private void Awake() {
            _net = GetComponent<NetSyncManager>();
            _net.startAutomatically = false;

            _cube = GameObject.CreatePrimitive(PrimitiveType.Cube).transform;
            if (Camera.main == null) {
                var cameraObject = new GameObject("Camera", typeof(Camera)) { tag = "MainCamera" };
                cameraObject.transform.position = new Vector3(0, 4, -10);
                cameraObject.transform.LookAt(Vector3.zero);
            }

            // Zero-copy event: the data is parsed here and not kept.
            _net.MessageReceived += (channel, data) => {
                if (channel != StateChannel || _net.IsServer) {
                    return; // the host already has the authoritative transform
                }
                var reader = new NetReader(data);
                _snapshots.Add(new Snapshot {
                    TimeMs = reader.ReadDouble(),
                    Position = reader.ReadVector3(),
                    Rotation = reader.ReadQuaternion()
                });
            };
        }

        private void Update() {
            if (_net.IsServer) {
                float angle = Time.time * orbitSpeed;
                _cube.position = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * orbitRadius;
                _cube.rotation = Quaternion.Euler(0, angle * Mathf.Rad2Deg, 0);
            }
            else if (_net.IsClient) {
                Interpolate(_net.ServerTimeMs - interpolationDelayMs);
            }
        }

        private void Interpolate(double renderTimeMs) {
            if (double.IsNaN(renderTimeMs)) {
                return; // no clock sync yet
            }
            // Keep one snapshot older than the render time, drop the rest.
            while (_snapshots.Count > 2 && _snapshots[1].TimeMs <= renderTimeMs) {
                _snapshots.RemoveAt(0);
            }
            if (_snapshots.Count < 2) {
                return;
            }
            var from = _snapshots[0];
            var to = _snapshots[1];
            float t = Mathf.Clamp01((float)((renderTimeMs - from.TimeMs) / (to.TimeMs - from.TimeMs)));
            _cube.position = Vector3.Lerp(from.Position, to.Position, t);
            _cube.rotation = Quaternion.Slerp(from.Rotation, to.Rotation, t);
        }

        private void FixedUpdate() {
            if (!_net.IsServer) {
                return;
            }
            _writer.Reset();
            _writer.WriteDouble(_net.ServerTimeMs).WriteVector3(_cube.position).WriteQuaternion(_cube.rotation);
            _net.Broadcast(StateChannel, _writer);
        }

        private void OnGUI() {
            GUILayout.BeginArea(new Rect(10, 10, 320, 150), GUI.skin.box);
            if (!_started) {
                GUILayout.Label("Tick Simulate Network on the manager to see interpolation hide a bad link.");
                if (GUILayout.Button("Host")) {
                    Begin(NetSyncManager.NetRole.Host);
                }
                if (GUILayout.Button("Join (LAN)")) {
                    Begin(NetSyncManager.NetRole.Client);
                }
            }
            else if (_net.IsServer) {
                GUILayout.Label($"Server clock {_net.ServerTimeMs:F0} ms, {_net.Server.ConnectionCount} connection(s)");
            }
            else if (_net.IsClient) {
                GUILayout.Label($"Server clock {_net.ServerTimeMs:F0} ms (±{_net.Client.ServerTimeAccuracyMs:F2} ms)");
                GUILayout.Label($"Ping {_net.Client.PingMs} ms, {_snapshots.Count} snapshots buffered");
            }
            else {
                GUILayout.Label("Connecting…");
            }
            GUILayout.EndArea();
        }

        private void Begin(NetSyncManager.NetRole role) {
            _net.role = role;
            _started = true;
            _net.StartNetwork();
        }
    }
}
