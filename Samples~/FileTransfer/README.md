# File Transfer sample

Streams a file of any size on a `ReliableOrdered` + `Low` channel while a real-time channel keeps running.

## Setup

1. Create an empty scene and add an empty GameObject with **FileTransferSample**.
2. Build and run two instances: **Receive files** in one, **Send files** in the other (LAN discovery connects them).
3. Press **Send a 32 MB test file** and watch the round trip of the real-time channel while it runs.

`SendStreamAsync` reads the file through backpressure, so memory stays bounded whatever the size. Create one `NetStreamReceiver` per sender and sanitize the announced name before using it as a path.
