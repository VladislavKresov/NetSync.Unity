# Relay Room sample

Player-hosted rooms through a relay, by room code, with UDP hole punching.

## Setup

1. Create an empty scene and add an empty GameObject with **RelayRoomSample**.
2. Build and run two instances on one machine. In the first press **Host a room** (it also runs the relay) and note the code.
3. In the second enter the code and press **Join by code**.

For real use run `NetRelayServer` on a machine with a public address and enter its address on both sides. The stream slot always goes through the relay; UDP goes direct when the NATs allow it and falls back to the relay when they don't. Traffic is end-to-end encrypted: the relay never sees the key.
