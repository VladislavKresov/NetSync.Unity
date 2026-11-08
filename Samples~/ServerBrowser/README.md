# Server Browser sample

LAN server list: announcements with a payload, plus connectionless queries for player count and ping.

## Setup

1. Create an empty scene and add an empty GameObject with **ServerBrowserSample**.
2. Build and run two instances. In one enter a name and press **Host**.
3. In the other press **Search the LAN**: the server appears with its player count and ping. **Join** connects.

`NetQuery` needs no connection, so the same technique works for an internet server list once you know the addresses. The handler runs on a network thread, and a reply is never larger than its query — `maxResponseSize` pads the request to the size you expect back.
