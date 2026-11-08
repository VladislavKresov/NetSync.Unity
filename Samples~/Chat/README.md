# Chat sample

LAN chat with typed messages (`INetMessage`) over the reliable TCP channel.

## Setup

1. Create an empty scene and add an empty GameObject with **ChatSample** (it adds the NetSync Manager).
2. Build the scene and run two instances (or one build and the Editor).
3. Press **Host** in one and **Join (LAN)** in the other: the client discovers the host automatically.
4. Messages are relayed by the server to every client.

The registry is set in `Awake`, handlers with `On<T>` / `OnServer<T>` — they work before networking starts and survive restarts.
