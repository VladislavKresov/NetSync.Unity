using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using NetSync;
using NetSync.Discovery;
using NetSync.Messaging;
using NetSync.Peers;
using NetSync.Relay;
using NetSync.Serialization;
using NetSync.Streams;
using NetSync.Transports;
using NetSync.Unity;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace NetSync.Unity.Tests {
    /// <summary>The adapter end to end on the real Unity runtime (Mono or IL2CPP), over loopback.</summary>
    public class NetSyncManagerTests {
        private const float TimeoutSeconds = 15f;

        private struct Ping : INetMessage {
            public int Value;
            public void Write(NetWriter writer) => writer.WriteVarInt(Value);
            public void Read(NetReader reader) => Value = reader.ReadVarInt();
        }

        private readonly List<GameObject> _objects = new List<GameObject>();

        private NetSyncManager Create(NetSyncManager.NetRole role, Action<NetSyncManager> configure = null) {
            var gameObject = new GameObject("NetSync " + role);
            gameObject.SetActive(false); // configure before Awake
            var manager = gameObject.AddComponent<NetSyncManager>();
            manager.role = role;
            manager.startAutomatically = false;
            manager.lanDiscovery = false;
            manager.autoReconnect = false;
            manager.port = 0;
            configure?.Invoke(manager);
            gameObject.SetActive(true);
            _objects.Add(gameObject);
            return manager;
        }

        private static IEnumerator Until(Func<bool> condition, string what) {
            float deadline = Time.realtimeSinceStartup + TimeoutSeconds;
            while (!condition()) {
                if (Time.realtimeSinceStartup > deadline) {
                    Assert.Fail("Timed out waiting for: " + what);
                }
                yield return null;
            }
        }

        private IEnumerator StartPair(NetSyncManager server, NetSyncManager client) {
            server.StartNetwork();
            yield return Until(() => server.IsServer, "server start");
            client.address = "127.0.0.1";
            client.port = (ushort)server.ServerPort;
            client.StartNetwork();
            yield return Until(() => client.IsClient, "client connect");
        }

        [TearDown]
        public void TearDown() {
            foreach (var gameObject in _objects) {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
            _objects.Clear();
        }

        [UnityTest]
        public IEnumerator Every_Default_Channel_Carries_Data_Both_Ways() {
            var server = Create(NetSyncManager.NetRole.Server);
            var client = Create(NetSyncManager.NetRole.Client);
            server.ServerMessageReceived += (connection, channel, data) => server.Send(connection, channel, data);
            var echoed = new HashSet<byte>();
            client.DataReceived += (channel, data) => {
                Assert.AreEqual(channel, data[0]);
                echoed.Add(channel);
            };
            yield return StartPair(server, client);

            float deadline = Time.realtimeSinceStartup + TimeoutSeconds;
            while (echoed.Count < 3 && Time.realtimeSinceStartup < deadline) {
                foreach (byte channel in new byte[] { 0, 1, 2 }) {
                    client.Send(channel, new[] { channel, (byte)1 });
                }
                yield return null;
            }
            Assert.AreEqual(3, echoed.Count, "channels echoed");
        }

        [UnityTest]
        public IEnumerator Host_Runs_Server_And_Local_Client() {
            var host = Create(NetSyncManager.NetRole.Host);
            int connected = 0;
            string received = null;
            host.Connected += () => connected++;
            host.ServerDataReceived += (connection, channel, data) => host.Broadcast(channel, data);
            host.DataReceived += (channel, data) => received = Encoding.UTF8.GetString(data);
            host.StartNetwork();
            yield return Until(() => host.IsHost, "host");
            Assert.AreEqual(1, connected);
            host.Send(0, Encoding.UTF8.GetBytes("hello"));
            yield return Until(() => received != null, "broadcast");
            Assert.AreEqual("hello", received);
        }

        [UnityTest]
        public IEnumerator Typed_Messages_Round_Trip_And_Handlers_Survive_A_Restart() {
            var registry = new NetMessageRegistry().Register<Ping>(1);
            var server = Create(NetSyncManager.NetRole.Server, m => m.Messages = registry);
            var client = Create(NetSyncManager.NetRole.Client, m => m.Messages = registry);
            int last = 0;
            server.OnServer<Ping>((connection, ping) => server.SendTyped(connection, 0, new Ping { Value = ping.Value + 1 }));
            client.On<Ping>(ping => last = ping.Value);
            yield return StartPair(server, client);
            client.SendTyped(0, new Ping { Value = 41 });
            yield return Until(() => last == 42, "typed echo");

            client.StopNetwork();
            yield return Until(() => !client.IsClient, "client stop");
            client.StartNetwork();
            yield return Until(() => client.IsClient, "client restart");
            client.SendTyped(0, new Ping { Value = 99 });
            yield return Until(() => last == 100, "typed echo after restart");
        }

        [UnityTest]
        public IEnumerator Preshared_Key_Protects_The_Connection() {
            void Secure(NetSyncManager manager) {
                manager.keyExchange = KeyExchangeMode.PresharedKey;
                manager.presharedKeyPassphrase = "test passphrase";
                manager.channels[0].encryption = true;
            }
            var server = Create(NetSyncManager.NetRole.Server, Secure);
            var client = Create(NetSyncManager.NetRole.Client, Secure);
            server.ServerDataReceived += (connection, channel, data) => server.Send(connection, channel, data);
            bool echoed = false;
            client.DataReceived += (channel, data) => echoed = data.Length == 3;
            yield return StartPair(server, client);
            Assert.AreNotEqual(CipherSuite.None, client.Client.CipherSuite);
            Debug.Log("[NetSync test] cipher suite: " + client.Client.CipherSuite);
            client.Send(0, new byte[3]);
            yield return Until(() => echoed, "encrypted echo");
        }

        [UnityTest]
        public IEnumerator Wrong_Preshared_Key_Is_Refused() {
            var server = Create(NetSyncManager.NetRole.Server, m => {
                m.keyExchange = KeyExchangeMode.PresharedKey;
                m.presharedKeyPassphrase = "right";
            });
            var client = Create(NetSyncManager.NetRole.Client, m => {
                m.keyExchange = KeyExchangeMode.PresharedKey;
                m.presharedKeyPassphrase = "wrong";
            });
            Exception failure = null;
            client.ConnectFailed += error => failure = error;
            LogAssert.ignoreFailingMessages = true;
            server.StartNetwork();
            yield return Until(() => server.IsServer, "server start");
            client.address = "127.0.0.1";
            client.port = (ushort)server.ServerPort;
            client.StartNetwork();
            yield return Until(() => failure != null, "refusal");
            LogAssert.ignoreFailingMessages = false;
            Assert.IsInstanceOf<ConnectionRejectedException>(failure);
            Assert.AreEqual(ConnectionRejection.Authentication, ((ConnectionRejectedException)failure).Rejection);
            Assert.IsNull(client.Client, "a failed client can start again");
        }

        [UnityTest]
        public IEnumerator Ecdh_With_A_Pinned_Identity_Connects_Or_Reports_Unsupported() {
            if (!NetIdentity.IsSupported) {
                Debug.Log("[NetSync test] ECDSA P-256 is not available on this runtime");
                Assert.Ignore("ECDSA P-256 is not available on this runtime");
            }
            using var identity = NetIdentity.Generate();
            var server = Create(NetSyncManager.NetRole.Server, m => {
                m.keyExchange = KeyExchangeMode.Ecdh;
                m.ServerIdentity = identity;
            });
            var client = Create(NetSyncManager.NetRole.Client, m => {
                m.keyExchange = KeyExchangeMode.Ecdh;
                m.trustedServerKeys.Add(Convert.ToBase64String(identity.PublicKey));
            });
            yield return StartPair(server, client);
            CollectionAssert.AreEqual(identity.PublicKey, client.Client.ServerIdentityKey);
            Debug.Log("[NetSync test] ECDH + identity works, suite " + client.Client.CipherSuite);
        }

        [UnityTest]
        public IEnumerator Approval_Refusal_Reaches_The_Client_With_Its_Reason() {
            var server = Create(NetSyncManager.NetRole.Server, m =>
                m.ConnectionApprover = request => request.ConnectData.Length == 4 ? ConnectionDecision.Accept() : ConnectionDecision.Reject("bad token"));
            var client = Create(NetSyncManager.NetRole.Client, m => m.ConnectData = new byte[3]);
            Exception failure = null;
            client.ConnectFailed += error => failure = error;
            server.StartNetwork();
            yield return Until(() => server.IsServer, "server start");
            client.address = "127.0.0.1";
            client.port = (ushort)server.ServerPort;
            client.StartNetwork();
            yield return Until(() => failure != null, "refusal");
            Assert.AreEqual("bad token", ((ConnectionRejectedException)failure).Reason);

            client.ConnectData = new byte[4];
            client.StartNetwork();
            yield return Until(() => client.IsClient, "approved connect");
        }

        [UnityTest]
        public IEnumerator Relay_Room_Connects_Host_And_Client() {
            var relay = new NetRelayServer();
            var start = relay.StartAsync(0);
            yield return Until(() => start.IsCompleted, "relay start");
            try {
                int relayPort = start.Result;
                var host = Create(NetSyncManager.NetRole.Server, m => {
                    m.useRelay = true;
                    m.relayAddress = "127.0.0.1";
                    m.relayPort = (ushort)relayPort;
                });
                host.ServerDataReceived += (connection, channel, data) => host.Send(connection, channel, data);
                host.StartNetwork();
                yield return Until(() => host.IsServer && host.RoomCode != null, "room");

                var client = Create(NetSyncManager.NetRole.Client, m => {
                    m.useRelay = true;
                    m.relayAddress = "127.0.0.1";
                    m.relayPort = (ushort)relayPort;
                    m.roomCode = host.RoomCode;
                });
                bool echoed = false;
                client.DataReceived += (channel, data) => echoed = channel == 2;
                client.StartNetwork();
                yield return Until(() => client.IsClient, "client through the relay");
                client.Send(2, new byte[2000]);
                yield return Until(() => echoed, "echo through the relay");
                Assert.Greater(relay.RelayedTcpBytes, 0);
            }
            finally {
                relay.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator Connectionless_Query_Is_Answered() {
            var server = Create(NetSyncManager.NetRole.Server, m =>
                m.QueryHandler = (from, request) => Encoding.UTF8.GetBytes("two players"));
            server.StartNetwork();
            yield return Until(() => server.IsServer, "server start");
            var query = NetQuery.QueryAsync(new IPEndPoint(IPAddress.Loopback, server.ServerPort), new byte[0], maxResponseSize: 64);
            yield return Until(() => query.IsCompleted, "query");
            Assert.AreEqual("two players", Encoding.UTF8.GetString(query.Result.Response));
        }

        [UnityTest]
        public IEnumerator Stream_Delivers_A_File_Intact_And_The_Clock_Syncs() {
            var server = Create(NetSyncManager.NetRole.Server);
            var client = Create(NetSyncManager.NetRole.Client);
            var destination = new MemoryStream();
            var receiver = new NetStreamReceiver(info => destination) { DisposeDestinations = false };
            bool done = false;
            receiver.StreamCompleted += info => done = true;
            server.ServerMessageReceived += (connection, channel, data) => {
                if (channel == 2) {
                    receiver.HandleMessage(data);
                }
            };
            yield return StartPair(server, client);

            var payload = new byte[3 * 1024 * 1024];
            new System.Random(5).NextBytes(payload);
            var send = client.Client.SendStreamAsync(2, new MemoryStream(payload), "blob");
            yield return Until(() => done, "stream");
            Assert.IsTrue(send.IsCompleted && !send.IsFaulted, "sender finished");
            CollectionAssert.AreEqual(payload, destination.ToArray());

            yield return Until(() => !double.IsNaN(client.ServerTimeMs), "clock sync");
            Assert.Less(Math.Abs(client.ServerTimeMs - server.ServerTimeMs), 100.0, "shared clock");
        }

        [UnityTest]
        public IEnumerator Stop_Raises_Disconnected_And_Server_Sees_It() {
            var server = Create(NetSyncManager.NetRole.Server);
            var client = Create(NetSyncManager.NetRole.Client);
            DisconnectReason? clientReason = null;
            DisconnectReason? serverReason = null;
            client.Disconnected += reason => clientReason = reason;
            server.ConnectionClosed += (connection, reason) => serverReason = reason;
            yield return StartPair(server, client);
            client.StopNetwork();
            Assert.AreEqual(DisconnectReason.Local, clientReason);
            yield return Until(() => serverReason != null, "server notices");
            Assert.AreEqual(DisconnectReason.Remote, serverReason);
        }
    }
}
