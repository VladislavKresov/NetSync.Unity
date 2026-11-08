# NetSync for Unity

Multiplayer and networking for Unity on top of [NetSync](https://github.com/VladislavKresov/NetSync): add one component, choose Host, Server or Client, and exchange data over TCP and UDP from the main thread.

The package contains the NetSync core as a DLL plus a thin Unity layer: the `NetSyncManager` component, console logging and `Vector`/`Quaternion` serialization.

## Why NetSync for Unity

- **Set up in the Inspector.** Role, channels, security, relay and reconnect are fields on one component.
- **Main-thread events.** Every `NetSyncManager` event runs in `Update`, so you can call Unity APIs from handlers.
- **TCP and UDP in one connection.** Each channel picks its transport and delivery mode: reliable chat next to fast position updates and low-priority downloads.
- **Works on Mono and IL2CPP.** Covered by PlayMode tests on Unity 2021.3 (Mono) and in a Unity 6000.2 IL2CPP Windows player.
- **No servers to configure for LAN or friends.** LAN discovery finds hosts automatically; a relay connects players by room code with UDP hole punching.
- **Network features included.** Typed messages, file streaming, pre-shared-key encryption, login approval, shared server clock and a latency and loss simulator.

## Getting started

Requires Unity **2021.2 or newer**.

1. In Package Manager choose *Add package from git URL…* and enter:

   ```
   https://github.com/VladislavKresov/NetSync.Unity.git
   ```

2. Add **NetSync → NetSync Manager** to a GameObject and set **Role** to `Host`, or to `Client` in the second instance. With **LAN discovery** on, clients find the host without an address.
3. Send and receive from your own script:

```csharp
using NetSync.Serialization;
using NetSync.Unity;
using UnityEngine;

public class PositionSync : MonoBehaviour {
    [SerializeField] private NetSyncManager net;
    private readonly NetWriter _writer = new NetWriter();

    private void OnEnable() {
        net.ServerMessageReceived += (connection, channel, data) => net.Broadcast(channel, data.ToArray());
        net.MessageReceived += (channel, data) => {
            if (channel == 1) transform.position = new NetReader(data).ReadVector3();
        };
    }

    private void FixedUpdate() {
        if (!net.IsClient) return;
        _writer.Reset();
        _writer.WriteVector3(transform.position);
        net.Send(1, _writer); // UDP, stale positions are dropped
    }
}
```

Default channels are below. You can edit them in the Inspector, but client and server must match.

| Channel | Transport | Delivery | Use for |
|---|---|---|---|
| 0 | TCP | reliable, ordered | commands, chat |
| 1 | UDP | `UnreliableSequenced` | positions, state |
| 2 | UDP | `ReliableOrdered` | large messages |

## Samples

Import them from Package Manager → **NetSync for Unity** → **Samples**. Each is one script for an empty GameObject; build and run two instances.

| Sample | Shows |
|---|---|
| Chat | typed messages, LAN discovery |
| Position Sync | UDP snapshots interpolated on the server clock |
| Secure Login | encrypted login and server-side approval |
| Server Browser | LAN server list with players and ping |
| File Transfer | large files next to real-time traffic |
| Relay Room | player-hosted rooms by room code |

## Before you ship

- **Security:** the default `None` mode is for trusted networks only. Use `PresharedKey`. `Ecdh` and server identity need P-256, which Unity 2021.3 (Mono) and a 6000.2 IL2CPP player don't have, so a Unity client can't join a server that requires `Ecdh`.
- **Platforms:** Windows is tested. Android and iOS use the same sockets but haven't been tested on devices. WebGL is not supported.
- **Background:** events arrive only while `Update` runs. Keep **Run In Background** on when testing two instances on one machine.

The full API is on `net.Client` and `net.Server`; see the [core documentation](https://github.com/VladislavKresov/NetSync/blob/main/docs/Home.md). To rebuild the bundled DLL, run `tools~/update-core.ps1 -CorePath ..\NetSync -Ref v2.0.0-rc.3`.

MIT License.
