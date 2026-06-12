# Multiplayer Pong (raw TCP + UDP sockets)

A networked version of the local Pong demo (`Assets/Demos/Pong/`). Built on the
project's raw socket helpers in `Assets/Demos/TCP/` and `Assets/Demos/UDP/` — no
high-level netcode is used, in line with the course constraint
(https://learn.glassworks.tech/mmporg/networking/architecture).

## Architecture

- **Client–server**, **client-with-local-control** sync paradigm.
- **Hybrid transport on the same port (25000):**
  - **TCP (reliable/ordered)** carries handshake and all event/control messages
    (`ASSIGN`, `ROSTER`, `NAMES`, `COLORS`, `SCORE`, `DAMAGE`, `WIN`, `RESET`,
    `COUNTDOWN`, `NAME`, `READY`…), plus the per-connection `UDPTOKEN`.
  - **UDP (real-time, lossy)** carries the high-frequency channel: `STATE`
    (server → clients) and `PADDLE` (client → server) at ~30 Hz. UDP avoids TCP
    head-of-line blocking, so a lost packet no longer stalls every later update.
- Each client owns and **moves its own line locally** (zero input lag) and
  streams its paddle angle over UDP.
- The server is the **referee**: it simulates the ball, resolves collisions,
  awards scores, and broadcasts authoritative `STATE` snapshots (with a sequence
  number, server timestamp, and ball velocity).
- Clients render with **snapshot interpolation** (~100 ms behind the latest
  server time) and **ball extrapolation** during packet gaps, which removes the
  jumps/teleporting seen on a poor connection. Tuning lives on `PongNetView`
  (`InterpolationDelayMs`, `MaxExtrapolationMs`).
- A client registers its UDP endpoint with `HELLO <token>` (resent until the
  first `STATE` arrives); `PADDLE` carries the same token, which also blocks
  trivial spoofing. Presence/disconnection is still governed by TCP.
- Messages are newline-delimited UTF-8 (see `Net/PongProtocol.cs` for the full
  wire format). On UDP, one datagram is exactly one message.

## Files

- `Net/PongProtocol.cs` — message types and framing.
- `Net/PongServer.cs` — TCP listener, per-client message buffer, broadcast.
- `Net/PongServerGame.cs` — authoritative game loop (ball, scoring, line
  assignment, per-line health hook for Milestone 2).
- `Net/PongClient.cs` — TCP connection, typed message events.
- `CircleArenaConfig.cs` / `PongCircleArena.cs` — circular ring + platform slots.
- `PongBootstrap.cs` — procedurally builds the scene (camera, ball, circle arena)
  and wires the right components based on a `Mode = Server | Client` role.
- `PongNetPaddle.cs` — locally-controlled line (InputSystem → local movement →
  send PADDLE to server).
- `PongNetView.cs` — renders ball + remote paddles from `STATE` with smoothing.
- `PongServerUI.cs` — optional server-side listen/host UI.
- `PongClientConnectOverlay.cs` — IMGUI connect screen + end-of-round menu
  (lost / match-over / awaiting / countdown panels).
- `PongServer.unity` / `PongClient.unity` — minimal scenes that just place a
  `PongBootstrap`.

## First-time setup (one click)

1. Open the project in Unity.
2. From the top menu, run **Tools > Pong > Generate Network Scenes**. This
   creates `Assets/Pong/PongServer.unity` and `Assets/Pong/PongClient.unity`.

Each scene contains a single `PongBootstrap` GameObject; at Play it builds the
camera, ball, and paddles, and wires the server or client components for you.

## Running locally (Editor)

1. Open `Assets/Pong/PongServer.unity` and press Play (listens on port **25000**).
2. Open `Assets/Pong/PongClient.unity` and press Play — enter `127.0.0.1` on the
   connect overlay, or launch with `-serverIP 127.0.0.1`.
3. Repeat for more clients. Use **← / →** to move on the circle.

## LAN / builds (two machines)

1. **Host** builds and runs **PongServer** only. Check the Unity console for
   `give clients IP: 192.168.x.x` (or run `ipconfig` on Windows / `ifconfig` on Mac).
2. Allow **TCP and UDP port 25000** through the host firewall (incoming). Both
   are required: TCP for control, UDP for real-time state/paddle updates.
3. **Clients** run the **PongClient** build. On the connect screen, enter the
   host's **LAN IP** (not `127.0.0.1`). Same Wi‑Fi / Ethernet LAN required.
4. Optional CLI: `./PongClient -serverIP 192.168.1.42 -serverPort 25000`

**Common mistakes:** wrong IP baked into the build, `127.0.0.1` on a remote PC,
host firewall blocking 25000, server not started before clients.

## Building a headless server

The course allows a headless (no-GUI) server.

1. `File → Build Profiles → Dedicated Server` (or `Server Build` checkbox on
   the platform build profile).
2. Set the only scene in **Scenes in Build** to `Assets/Pong/PongServer.unity`.
3. Build, then run the resulting executable with `-batchmode -nographics`:
   ```
   ./SuperPongServer -batchmode -nographics
   ```
4. Build the client similarly with `Assets/Pong/PongClient.unity`. Each client
   connects to the server's IP/port.

## Wire protocol summary

```
client -> server (TCP):
  NAME <displayName>
  READY | POSTGAME | SPECTATE

client -> server (UDP):
  HELLO  <token>
  PADDLE <token> <arcOffsetRadians>

server -> client (TCP):
  UDPTOKEN <token>
  ASSIGN <lineIndex> <lineCount>
  ROSTER <lineCount>
  NAMES <name0><tab><name1>...
  COLORS <slot0> <slot1> ... <slotN-1>
  SCORE  <lineIndex> <score>
  DAMAGE <lineIndex> <state>     (state: 0=Intact, 2=Eliminated)
  WIN    <lineIndex> <winnerName>
  RESET
  COUNTDOWN <secondsRemaining>
  JOINCOUNTDOWN <secondsRemaining>

server -> client (UDP):
  STATE  <seq> <serverTimeMs> <ballX> <ballY> <ballVX> <ballVY> <offset0> ... <offsetN-1>
```

All messages end with `\n`. TCP is a byte stream, so the receive side accumulates
bytes in `PongMessageBuffer` and yields complete `\n`-terminated lines. UDP is
message-oriented, so each datagram is one message (the trailing `\n` is trimmed
on receive). `STATE` carries a monotonic `seq` (clients drop stale/out-of-order
datagrams), a server timestamp, and the ball velocity used for interpolation and
extrapolation.

## Scaling toward "massive" Pong

- `PongServerGame.Lines` is a data-driven list (`List<LineConfig>`), so the
  same server loop generalizes from 2 lines to N.
- `STATE` carries N paddle Ys; `ASSIGN` carries the line count so clients can
  size their score table.
- Platforms are created dynamically (`ROSTER`): one bar per connected player.
  Shared geometry lives in `CircleArenaConfig` (radius, ring bounds, platform arc).

## Milestone 2 hooks (already in place)

- `PongServerGame.LineRuntime.Health` (0=Intact, 1=Scattered, 2=Broken).
- `OnBallHitLine(int lineIndex)` virtual server hook fires on every legit
  paddle hit. M2 increments health here and broadcasts `DAMAGE`.
- The ball simulation already skips lines with `Health >= 2` (broken: pass
  through).
- `DAMAGE` message type is defined in the protocol and parsed by the client.
