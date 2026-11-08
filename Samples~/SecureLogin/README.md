# Secure Login sample

ECDH key exchange with a pinned server identity, an encrypted login and server-side approval.

## Setup

1. Create an empty scene and add an empty GameObject with **SecureLoginSample**.
2. Build and run two instances on one machine: **Start server** in one, **Log in** in the other (`alice` / `secret`).
3. Try a wrong password: the refusal and the server's reason reach the client through `ConnectFailed`.

For a single-machine demo the server writes its public key to a temporary file and the client pins it. In a real game the public key ships with the client (`trustedServerKeys`, base64), and the private key stays in the server's secret store — set `ServerIdentity` from code, never from the scene.

Unity 2021.3 (Mono) and a 6000.2 IL2CPP player have neither ECDH nor ECDSA P-256 (`NetIdentity.IsSupported == false`), so there the sample runs with `PresharedKey`. The label at the top shows the active mode.
