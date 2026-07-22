# Interserver shuttle transfers

The long-range BSS transfers a complete FTL group (the piloted shuttle, FTLLocked docked shuttles,
magnet-latched shuttles, contents, and passenger bodies) between trusted servers. The destination chooses a random
free arrival area on a map explicitly allowed for the source server. No arrival beacons or save points are required.

## Configure two servers

Only administrators with the `Host` flag can open **Admin menu -> Long-range BSS**.

1. On each server, set a unique server ID, display name, public `ss14://` address, and enable the system.
2. Add the other server as a peer on both sides. `API URL` is the remote Robust status HTTP endpoint, not its game
   address. Both reciprocal peer records must contain the same secret (at least 16 characters).
3. On the destination's peer record, select the local logical map IDs that this source is allowed to enter. The UI
   lists currently loaded IDs; `FrontierSector` represents the main Frontier map.
4. Mark the peer approved, save it, and use **Test connection**. A map is advertised only while it exists and remains
   allowed on the destination.

The peer allowlist is directional. For example, maps configured on Beta's record for Alpha control Alpha -> Beta;
maps configured on Alpha's record for Beta control Beta -> Alpha.

Use HTTPS or a private authenticated network for the API endpoint. The shared secret is sent with each request and
must not cross an untrusted plaintext network.

## Transfer and recovery model

The protocol is authenticated and idempotent: reserve a collision-free area, upload a size-limited snapshot, then
commit destination ownership. The source removes its shuttle and redirects passengers only after a durable committed
response, or a later status check proving that commit succeeded. Ambiguous `Committing` states are retried and are
never rolled back merely because the network is unavailable.

On restart, both sides replay their journals. Stable ship IDs and monotonic ownership epochs prevent an old world save
from creating a second authoritative shuttle. Passengers reconnecting to the old server are redirected to the newest
known owner. A later incoming transfer supersedes that old route.

The client always shows the destination address while attempting automatic redial. This is intentional: custom
launchers may silently disable redial, so the address must remain available for manual connection.

## Storage and tuning

Runtime state is in the server user-data directory:

- `/interserver/peers.json` — trusted peer registry;
- `/interserver/outbox.json` and `/interserver/inbox.json` — durable transfer journals;
- `/interserver/snapshots/<transfer-id>.yml` — retained shuttle snapshots.

Journal updates keep `.bak` and `.tmp` fallbacks so a process failure during replacement does not silently reset
ownership state. Do not manually copy or delete these files while a server is running.

Advanced server-only CVars are `interserver.max_snapshot_mib`, `interserver.arrival_min_radius`,
`interserver.arrival_max_radius`, `interserver.arrival_travel_seconds`, and `interserver.reservation_seconds`.

## Pre-production failure checks

Test with disposable servers and a shuttle containing a passenger and a docked craft:

1. normal transfer in both directions;
2. destination unavailable before reserve (source must remain intact);
3. connection loss during upload (source must return and destination reservation must expire/abort);
4. response loss during commit (source must poll; it must never create two committed owners);
5. source restart after commit and destination restart while `Committing`;
6. launcher with redial disabled (manual address remains visible);
7. occupied arrival area and several simultaneous reservations (each must receive a non-overlapping location);
8. reconnect after transfer, then return to the original server (the stale redirect must no longer apply).
