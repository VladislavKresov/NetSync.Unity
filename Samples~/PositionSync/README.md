# Position Sync sample

Server-authoritative transform synchronization over sequenced UDP.

## Setup

1. Create a scene with a GameObject holding **NetSync → NetSync Manager**
   (default channel table: channel 1 = UDP UnreliableSequenced).
2. Add a Cube; add **PositionSyncSample** to it (it finds the manager automatically).
3. Build & run two instances: **Host** and **Client** (LAN discovery connects them).
4. Move the cube on the host with WASD / arrow keys — the client's cube follows,
   smoothed with a simple lerp.

Why `UnreliableSequenced`: positions are sent every FixedUpdate, so retransmitting a
lost snapshot is pointless — a newer one is already on the wire. The mode drops
out-of-date packets and never blocks on loss, keeping latency minimal.
