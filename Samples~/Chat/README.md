# Chat sample

Minimal chat over the reliable TCP channel, with LAN discovery.

## Setup

1. Create an empty scene.
2. Add an empty GameObject; add **NetSync → NetSync Manager** and **ChatSample** to it.
3. Keep the default channel table (channel 0 = TCP) and *LAN discovery* enabled.
4. Build the scene, run one instance with role **Host** and another with role **Client**
   (or run one in the Editor). The client discovers the host automatically.
5. Type into the box, press *Send* — messages are relayed by the server to every client.
