# Position Sync sample

Server-authoritative transform sync over sequenced UDP, interpolated on the server's clock.

## Setup

1. Create a scene and add an empty GameObject with **PositionSyncSample** (it adds the manager and spawns a cube).
2. Build and run two instances: **Host** and **Join (LAN)**. The host's cube orbits; the client's follows.
3. Tick **Simulate Network** on the manager (Editor and development builds) to add latency, jitter and loss.

Why it stays smooth: snapshots are stamped with `ServerTimeMs` and the client renders about 100 ms in the past on that same timeline, always between two received snapshots. `UnreliableSequenced` never waits for a lost packet — a newer one is already on the wire.
