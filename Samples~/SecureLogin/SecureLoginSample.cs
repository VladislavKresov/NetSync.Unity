using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using NetSync;
using NetSync.Peers;
using NetSync.Unity;
using UnityEngine;

namespace NetSync.Unity.Samples {
    /// <summary>
    /// A login that is safe on an open network: the server proves its identity (the client pins
    /// its public key), the password travels encrypted as connect data, and the server decides
    /// who joins. Falls back to a pre-shared key on runtimes without ECDH/ECDSA.
    /// Setup: empty scene, one GameObject with this component.
    /// </summary>
    [RequireComponent(typeof(NetSyncManager))]
    public class SecureLoginSample : MonoBehaviour {
        private static readonly Dictionary<string, string> Accounts = new Dictionary<string, string> {
            ["alice"] = "secret",
            ["bob"] = "hunter2"
        };

        private NetSyncManager _net;
        private NetIdentity _identity;
        private string _user = "alice";
        private string _password = "secret";
        private string _address = "127.0.0.1";
        private string _status = "";

        private bool Signed => NetIdentity.IsSupported;
        private string PublicKeyFile => Path.Combine(Application.temporaryCachePath, "netsync-server.pub");

        private void Awake() {
            _net = GetComponent<NetSyncManager>();
            _net.startAutomatically = false;
            _net.lanDiscovery = false;
            _net.autoReconnect = false;
            _net.channels.ForEach(channel => channel.encryption = channel.transport == TransportType.Tcp);

            if (Signed) {
                _net.keyExchange = KeyExchangeMode.Ecdh;
            }
            else {
                _net.keyExchange = KeyExchangeMode.PresharedKey;
                _net.presharedKeyPassphrase = "shared out of band";
            }

            _net.ConnectionApprover = Approve;
            _net.ConnectionOpened += connection => _net.Send(connection, 0, Encoding.UTF8.GetBytes($"Welcome, {connection.Tag}!"));
            _net.DataReceived += (channel, data) => _status = $"Server says: {Encoding.UTF8.GetString(data)}\nProtection: {_net.Client.CipherSuite}";
            _net.ConnectFailed += error => _status = error is ConnectionRejectedException rejected
                ? $"Refused ({rejected.Rejection}): {rejected.Reason}"
                : $"Failed: {error.Message}";
        }

        // Runs on the server, after the handshake: the connect data has already been decrypted.
        private ValueTask<ConnectionDecision> Approve(ConnectionRequest request) {
            var parts = Encoding.UTF8.GetString(request.ConnectData.Span).Split(new[] { ':' }, 2);
            if (parts.Length != 2 || !Accounts.TryGetValue(parts[0], out var password) || !SameText(password, parts[1])) {
                return ConnectionDecision.Reject("invalid user or password");
            }
            request.Connection.Tag = parts[0];
            return ConnectionDecision.Accept();
        }

        // Comparison time doesn't depend on where the strings differ.
        private static bool SameText(string a, string b) {
            byte[] first = Encoding.UTF8.GetBytes(a), second = Encoding.UTF8.GetBytes(b);
            int difference = first.Length ^ second.Length;
            for (int i = 0; i < Math.Min(first.Length, second.Length); i++) {
                difference |= first[i] ^ second[i];
            }
            return difference == 0;
        }

        private void StartServer() {
            if (Signed) {
                // A real server loads this from a secret store; it must never ship in the client or the scene.
                _identity = NetIdentity.Generate();
                _net.ServerIdentity = _identity;
                File.WriteAllBytes(PublicKeyFile, _identity.PublicKey); // what clients pin
            }
            _net.role = NetSyncManager.NetRole.Server;
            _net.StartNetwork();
            _status = Signed ? $"Server identity {_identity.Fingerprint}" : "No ECDSA on this runtime: using a pre-shared key";
        }

        private void Login() {
            _status = "Logging in…";
            if (Signed && File.Exists(PublicKeyFile)) {
                _net.trustedServerKeys.Clear();
                _net.trustedServerKeys.Add(Convert.ToBase64String(File.ReadAllBytes(PublicKeyFile))); // ships with a real client
            }
            _net.ConnectData = Encoding.UTF8.GetBytes($"{_user}:{_password}");
            _net.role = NetSyncManager.NetRole.Client;
            _net.address = _address;
            _ = _net.StartClientAsync();
        }

        private void OnGUI() {
            GUILayout.BeginArea(new Rect(10, 10, 420, 280), GUI.skin.box);
            GUILayout.Label(Signed ? "Mode: ECDH + server identity" : "Mode: pre-shared key");
            if (!_net.IsServer && !_net.IsClient) {
                if (GUILayout.Button("Start server")) {
                    StartServer();
                }
                GUILayout.Space(10);
                _address = GUILayout.TextField(_address);
                _user = GUILayout.TextField(_user);
                _password = GUILayout.PasswordField(_password, '*');
                if (GUILayout.Button("Log in")) {
                    Login();
                }
            }
            else if (_net.IsClient && GUILayout.Button("Log out")) {
                _net.StopNetwork();
                _status = "";
            }
            GUILayout.Space(10);
            GUILayout.Label(_status);
            GUILayout.EndArea();
        }

        private void OnDestroy() => _identity?.Dispose();
    }
}
