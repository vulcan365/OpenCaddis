# OpenCaddis

OpenCaddis is under active development.

## Local FabrCore cloud configuration

`OpenCaddis.App` hosts a loopback FabrCore Cloud Server at `http://localhost:5082/`.
Open the Server page's **Cloud configuration** tab to maintain separate `fabrcore.json`
documents for OpenCaddis Server and OpenCaddis Server Builder. Each mode must have at least
one valid model and API key before it can start.

The documents and generated per-mode cloud authentication keys are stored as plaintext in the
app-data `CloudServer` folder. They persist across app restarts; this local store is intentionally
simple and is not a secrets vault.
