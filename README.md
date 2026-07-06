# NetSync for Unity

Unity adapter for [NetSync](../NetSync) — a standalone, zero-dependency networking core (TCP + UDP channels, reliable UDP, optional compression and encryption, LAN discovery).

The core lives in its own repository and ships here as a precompiled `netstandard2.1` DLL (`Runtime/Plugins/NetSync.dll`). This package adds what is Unity-specific and nothing more:

| Component | Purpose |
|---|---|
| `NetSyncManager` | MonoBehaviour facade: pick a role (Host / Server / Client), configure channels in the Inspector, get all events on the main thread |
| `UnityNetLogger` | Core log output → Unity console |
| `NetWriterUnityExtensions` | `Vector2` / `Vector3` / `Quaternion` for `NetWriter` / `NetReader` |

## Requirements

Unity **2021.2+** (.NET Standard 2.1 profile).

## Install

Package Manager → *Add package from git URL…*:

```
https://github.com/<you>/NetSync.Unity.git
```

or clone next to your project and *Add package from disk…* → `package.json`.

## Samples

Package Manager → select **NetSync for Unity** → **Samples** tab → *Import*:

| Sample | Shows |
|---|---|
| **Chat** | reliable TCP channel, LAN discovery, server relay/broadcast |
| **Position Sync** | sequenced UDP state sync (stale snapshots dropped), server-authoritative movement |

Each sample is a single script plus a README with 3-step scene setup — no scene files to untangle.

## Quick start

1. Add **NetSync → NetSync Manager** to a GameObject.
2. Pick a role. `Host` runs a server and connects a local client to it.
3. Leave `address` empty and keep **LAN discovery** on — clients find the server automatically (matched by `appId`, defaulting to `Application.productName`).

```csharp
using NetSync.Serialization;
using NetSync.Unity;
using UnityEngine;

public class PositionSync : MonoBehaviour {
    [SerializeField] private NetSyncManager net;
    private readonly NetWriter _writer = new NetWriter();

    private void OnEnable() {
        net.ServerDataReceived += (conn, channel, data) => net.Broadcast(channel, data);
        net.DataReceived += (channel, data) => {
            if (channel == 1) transform.position = new NetReader(data).ReadVector3();
        };
    }

    private void FixedUpdate() {
        if (!net.IsClient) return;
        _writer.Reset();
        _writer.WriteVector3(transform.position);
        net.Send(1, _writer.ToArray()); // channel 1: UDP UnreliableSequenced — stale positions are dropped
    }
}
```

Default channel table (edit in the Inspector; **client and server must match**):

| id | Transport | Reliability | Use for |
|---|---|---|---|
| 0 | TCP | — (TCP is reliable-ordered) | commands, chat |
| 1 | UDP | UnreliableSequenced | positions, state snapshots |
| 2 | UDP | ReliableOrdered | files, large messages |

Encryption: set **Key Exchange** to `PresharedKey` (same passphrase on both sides) or `Ecdh`, then tick `encryption` on the channels that need it.

## Updating the core DLL

```
powershell -File tools~/update-core.ps1 -CorePath path\to\NetSync
```

Builds the core repo and refreshes `Runtime/Plugins/NetSync.dll` + XML docs.

## Threading notes

The core runs its networking on background threads; this package uses the core's *Polled* delivery mode and drains events in `Update()`, so every `NetSyncManager` event fires on the Unity main thread. If you bypass the manager and use `NetClient`/`NetServer` directly, either call `PollEvents()` yourself or switch to `Immediate` mode and marshal to the main thread on your own.
