using NetSync.Serialization;
using NetSync.Unity;
using UnityEngine;

namespace NetSync.Unity.Samples {
    /// <summary>
    /// Server-authoritative transform sync. The host/server moves this object with
    /// the arrow keys / WASD; connected clients follow. Snapshots ride channel 1
    /// (UDP UnreliableSequenced): a lost snapshot is simply skipped and a stale one
    /// is dropped — exactly what you want for continuously-updated state.
    /// </summary>
    public class PositionSyncSample : MonoBehaviour {
        private const byte StateChannel = 1;

        [SerializeField] private NetSyncManager net;
        [SerializeField] private float moveSpeed = 5f;
        [SerializeField] private float lerpSpeed = 15f;

        private readonly NetWriter _writer = new NetWriter();
        private Vector3 _targetPosition;
        private Quaternion _targetRotation;
        private bool _hasTarget;

        private void Awake() {
            if (net == null) {
                net = FindObjectOfType<NetSyncManager>();
            }
            _targetPosition = transform.position;
            _targetRotation = transform.rotation;

            net.DataReceived += (channel, data) => {
                // The host's own client also receives broadcasts; it already has
                // the authoritative transform, so only pure clients apply them.
                if (channel != StateChannel || net.IsServer) {
                    return;
                }
                var reader = new NetReader(data);
                _targetPosition = reader.ReadVector3();
                _targetRotation = reader.ReadQuaternion();
                _hasTarget = true;
            };
        }

        private void Update() {
            if (net.IsServer) {
                var move = new Vector3(Input.GetAxis("Horizontal"), 0f, Input.GetAxis("Vertical"));
                transform.position += move * (moveSpeed * Time.deltaTime);
            }
            else if (_hasTarget) {
                transform.position = Vector3.Lerp(transform.position, _targetPosition, lerpSpeed * Time.deltaTime);
                transform.rotation = Quaternion.Slerp(transform.rotation, _targetRotation, lerpSpeed * Time.deltaTime);
            }
        }

        private void FixedUpdate() {
            if (!net.IsServer) {
                return;
            }
            _writer.Reset();
            _writer.WriteVector3(transform.position).WriteQuaternion(transform.rotation);
            net.Broadcast(StateChannel, _writer.ToArray());
        }
    }
}
