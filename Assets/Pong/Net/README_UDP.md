# Guide de présentation — transport UDP hybride (TCP + UDP)

Ce document décrit **où se trouve quoi** dans le code et **dans quel ordre** expliquer
l'architecture lors d'une soutenance. Tous les fichiers cités sont sous `Assets/Pong/`.

---

## 1. Problème résolu (30 secondes)

Avant UDP, **tout** passait en TCP (~30 updates/s). Deux problèmes :

| Problème TCP | Symptôme en jeu | Fichier où on le voit |
|---|---|---|
| **Nagle** (petits paquets bufferisés) | Input paddle retardé ~40 ms | `PongServer.cs` / `PongClient.cs` → `ConfigureSocket` (`NoDelay = true`) |
| **Head-of-line blocking** (1 paquet perdu bloque les suivants) | Balle / paddles qui **sautent** | Migration vers UDP pour `STATE` + `PADDLE` |
| Rendu « snap to last state » | Pas d'interpolation entre deux updates | `PongNetView.cs` → buffer de snapshots |

**Solution retenue :** TCP pour le **fiable** (connexion, lobby, scores), UDP pour le **temps réel** (état monde + input paddle).

---

## 2. Vue d'ensemble (schéma à dessiner au tableau)

```
┌──────────── CLIENT ────────────┐          ┌──────────── SERVEUR ───────────┐
│                                │          │                                │
│  PongNetPaddle                 │  PADDLE  │  PongServer.HandleUdpDatagram   │
│    └─ SendPaddle() ────────────┼── UDP ──►│    └─ OnUdpPaddle              │
│                                │          │         └─ PongServerGame       │
│  PongClient                    │          │              .HandleUdpPaddle() │
│    ├─ TCP: READY, NAME, …      │  TCP     │  PongServer                    │
│    └─ UDP: HELLO, reçoit STATE │◄────────►│    ├─ AcceptNewConnections     │
│                                │          │    ├─ UDPTOKEN → client        │
│  PongNetView                   │  STATE   │    └─ BroadcastStateUdp()      │
│    └─ interpolation/extrapol.  │◄── UDP ──│         └─ PongServerGame       │
│                                │          │              .BroadcastState()  │
└────────────────────────────────┘          └────────────────────────────────┘
         ▲                                           ▲
         │                                           │
    PongUdpSocket.cs                          PongUdpSocket.cs
    (Bind, Send, Poll)                        (Bind port 25000, Poll)
         ▲                                           ▲
         │                                           │
              PongProtocol.cs  (Format*/TryParse*, messages)
```

**Port unique 25000** : TCP listener + UDP socket sur le même numéro.

---

## 3. Cycle de vie d'une connexion (ordre chronologique)

| Étape | Qui | Message | Transport | Code |
|------|-----|---------|-----------|------|
| 1 | Client → Serveur | (TCP connect) | TCP | `PongClient.Connect()` |
| 2 | Client | Ouvre socket UDP éphémère | UDP | `PongClient.OpenUdpChannel()` → `PongUdpSocket.Bind(0)` |
| 3 | Serveur → Client | `UDPTOKEN <secret>` | TCP | `PongServer.AcceptNewConnections()` |
| 4 | Client → Serveur | `HELLO <token>` | UDP | `PongClient.Dispatch` (UDPTOKEN) + `PumpUdp()` (retry) |
| 5 | Serveur | Enregistre `UdpEndpoint` du client | — | `PongServer.HandleUdpDatagram` (HELLO) |
| 6 | Serveur → Client | `ASSIGN`, `ROSTER`, … | TCP | `PongServerGame.BroadcastRosterAndAssign()` |
| 7 | Boucle jeu | `PADDLE <token> <angle>` | UDP ~30/s | `PongNetPaddle` → `PongClient.SendPaddle()` |
| 8 | Boucle jeu | `STATE <seq> <time> …` | UDP ~30/s | `PongServerGame.BroadcastState()` |
| 9 | Client | Filtre `seq`, interpole | — | `PongClient.DispatchUdp()` → `PongNetView` |

Le **token UDP** lie une connexion TCP à un endpoint UDP sans authentification lourde : seul le détenteur du token peut envoyer des `PADDLE` valides.

---

## 4. Fichiers — rôle de chacun

| Fichier | Rôle dans la couche UDP |
|---------|-------------------------|
| **`Net/PongProtocol.cs`** | Contrat wire : constantes, `Format*` / `TryParse*`, token aléatoire. **Point d'entrée pour la spec.** |
| **`Net/PongUdpSocket.cs`** | Transport bas niveau : bind, send non-bloquant, poll avec budget anti-freeze. |
| **`Net/PongServer.cs`** | Serveur : TCP + UDP sur le même port, table `_byToken`, `BroadcastStateUdp`, `HandleUdpDatagram`. |
| **`Net/PongClient.cs`** | Client : canal UDP après TCP, HELLO retry, filtre seq, `PongStateSnapshot`. |
| **`Net/PongServerGame.cs`** | Autorité : `BroadcastState()` (seq + horodatage + vélocité balle), `HandleUdpPaddle()`. |
| **`PongNetPaddle.cs`** | Input local → envoi UDP throttle (~30 Hz). |
| **`PongNetView.cs`** | Rendu : buffer snapshots, horloge de rendu retardée, interpolation + extrapolation balle. |
| **`Net/PongMessageBuffer`** (fin de `PongProtocol.cs`) | **TCP seulement** : reconstitue les lignes `\n` sur un flux octet. |

Fichiers **hors UDP** mais utiles en présentation : `PongClientConnectOverlay.cs` (UI), `CircleArenaConfig.cs` (physique balle).

---

## 5. Messages UDP (détail à connaître)

### Client → Serveur

```
HELLO  <token>
PADDLE <token> <ringAngleRadians>
```

- **HELLO** : « voici mon adresse UDP pour ce token ». Renvoyé jusqu'au premier STATE.
- **PADDLE** : position angulaire du paddle (~30/s). Le token identifie le joueur.

### Serveur → Client

```
STATE <seq> <serverTimeMs> <ballX> <ballY> <ballVX> <ballVY> <angle0> … <angleN-1>
```

| Champ | Pourquoi |
|-------|----------|
| `seq` | Numéro monotone ; le client **ignore** `seq <= lastSeq` (UDP désordonné) |
| `serverTimeMs` | Horodatage serveur pour synchroniser l'horloge de rendu client |
| `ballVX`, `ballVY` | Vélocité pour **extrapoler** la balle entre deux STATE perdus |
| `angle0…N` | Positions paddles **autoritaires** (sauf le sien : client-with-local-control) |

---

## 6. Côté client — lissage visuel (`PongNetView`)

1. **`HandleState`** : reçoit un `PongStateSnapshot`, pousse dans `_snaps`.
2. **`AdvanceRenderClock`** : avance une horloge locale **~100 ms derrière** le dernier snapshot (`InterpolationDelayMs`).
3. **`SampleWorld`** :
   - entre deux snapshots → **interpolation linéaire** (balle + angles distants),
   - après le dernier snapshot → **extrapolation** balle (`pos + vel * dt`), clampée au rayon arène.
4. Le paddle **local** n'est pas interpolé depuis le réseau : il est piloté par `PongNetPaddle` (zero input lag).

Paramètres Inspector sur `PongNetView` : `InterpolationDelayMs`, `MaxExtrapolationMs`, `MaxBufferMs`.

---

## 7. Robustesse (points forts à mentionner)

| Mécanisme | Fichier | Effet |
|-----------|---------|-------|
| `MaxDatagramsPerPoll = 2048` | `PongUdpSocket` | Évite de freeze une frame si flood UDP |
| Filtre `seq` stale | `PongClient.DispatchUdp` | Pas de retour arrière visuel |
| Trim `\n` sur UDP | `PongUdpSocket.Poll` | Messages `Format*` compatibles TCP/UDP |
| `RecoverBall` + cap rebond | `PongServerGame` / `CircleArenaConfig` | Serveur ne freeze plus sur boucle physique |
| Token 64-bit aléatoire | `PongProtocol.NewUdpToken` | Spoofing trivial de PADDLE évité |

---

## 8. Démo / test rapide

1. Lancer `PongServer.unity` puis `PongClient.unity` (port **25000 TCP + UDP**).
2. Simuler mauvaise connexion : augmenter `InterpolationDelayMs` sur le client → plus lisse mais plus de lag.
3. Logs diagnostic serveur : `Application.persistentDataPath/pong-diag.log` (`PongDiagnostics.cs`).

---

## 9. Structure du dossier `Assets/Pong/`

```
Assets/Pong/
├── Net/                    ← Réseau (TCP + UDP) — cœur de la soutenance
│   ├── PongProtocol.cs
│   ├── PongUdpSocket.cs
│   ├── PongServer.cs
│   ├── PongClient.cs
│   ├── PongServerGame.cs
│   ├── PongNetworkUtil.cs
│   ├── PongDiagnostics.cs
│   └── README_UDP.md       ← ce fichier
├── CircleArenaConfig.cs    ← Géométrie + physique balle
├── PongNetView.cs          ← Rendu interpolé (consomme STATE UDP)
├── PongNetPaddle.cs        ← Input → PADDLE UDP
├── PongClientConnectOverlay.cs
├── PongBootstrap.cs
├── PongServer.unity / PongClient.unity
└── README.md               ← Setup & protocole complet

Assets/Resources/Fonts/     ← Police pixel VT323 (menus)
Assets/Demos/               ← Démos cours (TCP/UDP/Pong local) — référence, pas le jeu réseau
```

---

## 10. Ordre suggéré pour présenter le code (5–10 min)

1. **`PongProtocol.cs`** — spec messages, pourquoi TCP vs UDP.
2. **`PongUdpSocket.cs`** — transport minimal (pas de magie Unity).
3. **`PongServer.cs`** — accept TCP, token, HELLO/PADDLE, broadcast STATE.
4. **`PongClient.cs`** — miroir client, filtre seq, HELLO retry.
5. **`PongServerGame.BroadcastState` + `HandleUdpPaddle`** — autorité serveur.
6. **`PongNetView`** — pourquoi on interpole (lien direct avec qualité perçue).
7. **`PongNetPaddle.SendPaddle`** — client-with-local-control.
