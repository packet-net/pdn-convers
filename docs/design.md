# pdn-convers — design

**What:** a ground-up packet-radio **convers (round-table chat) client+node** for the pdn node platform, giving pdn's RF users — and the node owner via a web tile — a first-class interface to the worldwide **convers network** ("Tampa PingPong" / WW Convers; the `conversd-saupp` lineage, DC6IQ → KO4KS → DL9SAU). It joins that network as a **single leaf node** over one upstream host link, and presents every local pdn user as an authenticated convers user. Companion spec: the protocol is `conversd-saupp/doc/SPECS` (the host-command set) plus the USER command grammar in `user.c`; both are pinned by golden transcripts captured from a real `conversd`.

**Rules of the road** (mirrors pdn-bbs, Tom 2026-06-11): own repo (`m0lte/pdn-convers`); **public interfaces only** — the app reaches pdn exclusively through RHPv2 (the network plane any app gets), its web UI rides the app-gateway identity contract, and it ships as a standard `pdn-app.yaml` package. pdn contains zero convers-specific code. Build it ourselves end-to-end (USER surface **and** the HOST peer link), wire-compatible and oracle-pinned — the same posture pdn-bbs took with FBB rather than shelling out to BPQMail.

## Why a node, not a dumb proxy

The cheap version (expect+nc, à la PE1RRR) logs each RF user into a remote conversd as a client. We reject it for the same reason pdn-bbs implemented FBB natively: it gives no presence model, no web client, no persistence, no per-user identity, and no control. Instead pdn-convers **is** a convers node — it speaks `/..HOST` up to one parent, holds the network's destination/channel/user table in memory, and bridges that to local RF and web sessions. As a strict leaf this is far simpler than a hub (see decision 1).

## Architecture

```
pdn-convers.sln
  src/Convers.Protocol   the wire (sans-IO): line codec for the convers '/'-grammar; the
                         USER command set (/name /msg /join /who /topic /pers …) and the
                         HOST command set (/..USER /..CMSG /..UMSG /..UDAT /..INVI
                         /..PING /..PONG /..TOPI /..MODE /..OPER /..AWAY /..OBSV /..DEST
                         /..ROUT /..SYSI /..LOOP /..ECMD …); the connection-state FSM
                         (UNKNOWN → USER | OBSERVER | HOST) for BOTH roles; parse/emit
                         only — no sockets. Golden-vector-pinned against captured conversd
                         transcripts + doc/SPECS.
  src/Convers.Core       domain: the in-memory ConversHub — channels, local users, the
                         network user/destination table, topics, personal text, away
                         state, loop/TTL guard — exposed sans-IO as Advance(event)->actions
                         (a local user joins/speaks/leaves, or a HostCommand arrives ⇒
                         fan-out actions to local sessions + the uplink). Plus a SQLite
                         store for the persisted bits conversd-saupp is known for: per-user
                         personal text, nicknames, passwords, and channel topics.
                         TimeProvider-driven throughout.
  src/Convers.Console    the terse RF user surface over an abstract session stream:
                         plain-language chat by default (join/say/who/msg/topic/leave/quit,
                         sentences not '/'-folklore), with a per-user `classic` mode that
                         exposes the literal conversd '/'-commands for power users and
                         legacy/automated clients. Auto-login from the RHP-authenticated
                         callsign — the user never types /name. Paclen-friendly, paged.
  src/Convers.Host       composition: RHPv2 client (RhpV2.Client) binding the convers
                         callsign — inbound accept → a local USER session on the hub;
                         the single UPSTREAM host link (RHP open to an RF neighbour, or a
                         direct TCP socket to an internet hub — decision 6) speaking
                         /..HOST with reconnect/backoff + PING/PONG keepalive; ASP.NET
                         loopback web chat (gateway identity headers, no second auth);
                         YAML config; pdn-app.yaml manifest (service + ui blocks).
  tests/                 unit per project + golden transcript vectors; tests/Convers.
                         Interop.Tests vs a real conversd-saupp container (the oracle lane).
  docker/                the oracle compose: conversd-saupp (built from the pinned source)
                         as our upstream peer + a second instance + netsim, mirroring
                         packet.net's interop-stack patterns.
```

Dependency rule: Protocol and Core never reference Host; Console references Core only; Host references all three. Nothing references packet.net source — only published packages (`RhpV2.Client` if consumable, else a minimal in-repo client to the pinned RHP wire).

## Load-bearing decisions

1. **Strict leaf topology — one primary uplink, never transit.** The convers net is a tree and "the network does not like loops" (`conversd.conf`). pdn-convers holds exactly one upstream host link and N local users; it is never a transit between two peers. This collapses the hard parts of the protocol: the SPECS rule "relay unrecognised `/..` to all *other* connected hosts" has, for a leaf, no other host to relay to — so we bridge local-users ⇄ the one uplink and nothing more. Loop guard (`/..LOOP`, TTL on `/..ROUT`) is still honoured defensively. Accepting a *downstream* peer is a post-v1 toggle, gated by an allowlist (decision 4).

2. **Sans-IO protocol + hub.** `Advance(line) -> actions` for the wire; `ConversHub.Advance(event) -> actions` for the model. Both host and user roles in one FSM, tested against scripted transcripts (including the facility-negotiation matrix from SPECS: `a/A` away old/new, `d` dest-forwarding, `m` modes, `p` ping-pong, `u` udat/user, `n` TNOS nicks). No component touches a socket; Host is the only I/O.

3. **Inbound demux — users by default.** One RHP-bound callsign. Unlike the BBS (which sniffs SID to split user-vs-forwarding-partner), nearly every inbound RF connect here is a human **USER**, so the demux greets and starts a Console session immediately and auto-logs them in from `child.RemoteCallsign`. A first line of `/..HOST …` is the only ambiguity — handled only if inbound peering is enabled (decision 4); otherwise it's treated as ordinary (invalid) user input. No silent-peek deadlock to design around.

4. **Identity & trust.** The RHP layer already authenticated the AX.25 callsign, so a local user is an authenticated ham — presented upstream as authenticated (no conversd `~` brand) and never spoofable; the user cannot pick an arbitrary `/name`. Our **upstream** host link carries whatever secret/allowlist the parent requires (the `Access … HOST` + optional password the hub op configures — held in `convers.yaml`). Inbound HOST peering, when enabled, is an explicit callsign allowlist mirroring conversd's `Access HOST`. The node's **own** callsign is not hand-set: it auto-derives from the host node per the pdn convention that *an app lives at an SSID of the node callsign* — `<PDN_NODE_CALLSIGN-base>-<ssid>` (default SSID 4), probe-walking to the next free SSID on a bind clash (the RHP-bind step, W5); an explicit `callsign:` in `convers.yaml` overrides it verbatim. This mirrors DAPPS's `<nodecall>-7` zero-config identity (packet.net `AppServiceSupervisor`).

5. **Presence bridging is the core runtime.** `ConversHub` maintains the network user/destination table and channel membership (local + remote). Local actions emit `/..USER`/`/..CMSG`/`/..UMSG`/`/..UDAT`/`/..INVI` upstream; inbound host commands fan out to the right local sessions (`/..CMSG` → every local user on that channel; `/..UMSG` → the addressed local session; `/who` answered from the table). PING/PONG keepalive measures and holds the link.

6. **Uplink transport — OPEN DECISION (needs Tom).** Two ways onto the network, both supportable behind one `IUpstreamLink`:
   - **RF via RHP `open`** (pdn-native): dial a neighbouring convers node on the band exactly as the BBS dials a forwarding partner. Stays inside the "public interfaces only" rule; no internet dependency.
   - **Internet TCP to a hub** (e.g. HubNA `44.68.41.2:3600`, or the EU/RRRWWC hub): the app opens its own outbound socket. Reaches the whole world immediately, but needs a 44Net source or the hub op to allowlist packet.net's public IP/FQDN as `Access … HOST`.
   Recommendation: implement `IUpstreamLink` with both providers; default to whichever matches packet.net's actual connectivity. This is the one fork that changes deployment, not code shape — flagged at the end.

7. **Storage.** One SQLite db in `PDN_APP_STATE` (resilient-open, schema-versioned): persisted personal text, nicknames, per-user passwords, and channel topics — the saupp differentiators that should survive restarts. Live channel/presence state is in-memory only (rebuilt from the uplink on reconnect).

8. **Web chat tile.** ASP.NET on the loopback upstream the manifest names; `X-Pdn-Gateway: 1` + `X-Pdn-User` headers are the auth boundary (no second login). pdn usernames map ↔ callsigns via the app's user table, so the owner and web users join the same channels as RF users — the convers analogue of webmail.

9. **Plain-language default, classic as a per-user preference** (verbatim from pdn-bbs's mandate). Canonical commands are words (`join 3333`, `who`, `msg g4abc …`, `topic …`, `leave`, `quit`); any unambiguous prefix works; `help` explains in sentences. `interface: classic` exposes the literal conversd `/`-surface for Winpack-era automated clients; the session engine picks the surface by callsign at connect. The uplink wire is unaffected either way.

10. **Oracle-first.** Every host-protocol behaviour lands with (a) a transcript test from SPECS, then (b) an assertion against a live `conversd-saupp` container before it's called done — the diff-oracle discipline that paid off in RHPv2 and pdn-bbs. We build conversd from the pinned source (its `Makefile` + `etc/*.rc.d` are in-tree) and run it as our parent.

## Build waves

- **W0** scaffold: repo, sln, CPM, CI (self-hosted runner, packet.net conventions), docs in-repo, empty-green test lanes, `pdn-app.yaml`, release/deploy scripts cloned from pdn-bbs.
- **W1** `Convers.Protocol`: the `/`-codec, USER + HOST command sets, the UNKNOWN→USER/OBSERVER/HOST FSM, facility negotiation. Golden vectors captured from a real conversd. Sub-agent; SPECS + `user.c`/`host.c` carry everything.
- **W2** `Convers.Core`: `ConversHub` model + SQLite persistence (personal text / nick / password / topics) + TimeProvider. Sub-agent, parallel with W1.
- **W3** `Convers.Console`: plain-language chat surface + classic `/`-mode over fakes (parallel once Core interfaces pin).
- **W4** the upstream **HostLink** FSM (we-dial-parent, and they-dial-us when downstream peering is enabled) + scripted-peer transcript suite + reconnect/backoff/keepalive (needs W1).
- **W5** `Convers.Host`: RHP client + inbound demux (RF user sessions) + `IUpstreamLink` (RF-open and TCP providers) + web chat + manifest + config (needs W2–W4).
- **W6** oracle: conversd-saupp docker lane green both directions; lab deploy as a package next to bbs/WALL/LOBBY; live demo — an RF user chats on a real convers channel via the uplink, observed on the real conversd, and a network user's message reaches the RF user.
- **W7** SHOULD wave: channel modes (`/..MODE` +i/+l/+m/+p/+s/+t), `/..OPER`, away (`/..AWAY` new+old), `/..TOPI` persistence + propagation, `/..ROUT`/`/..SYSI`, link-time `p` measurement, the `compression`/`u` extensions, downstream peering toggle, and classic-client niceties.

## Release & deploy (cloned from pdn-bbs)

Versioned Debian `.deb` per arch (amd64/arm64/armhf), self-contained binary + manifest under `/usr/share/packetnet/apps/convers`. State (`convers.db`, `convers.yaml`) in `/var/lib/packetnet/apps/convers`, preserved across upgrades. Push a `v*` tag (or run `publish-convers`) to cut a Release; `scripts/deploy-convers.sh` for the build→deploy→show loop against the lab box. First run writes a commented default `convers.yaml` (callsign `N0CALL` placeholder, the uplink block, default channel, sysop) and keeps running until edited.

## pdn-app.yaml (sketch)

```yaml
manifest: 1
id: convers
name: Convers
version: "0.1.0"
description: Round-table packet chat — a leaf node on the worldwide convers network, for RF and web users.
icon: chat
capabilities: [network, web]   # binds its own callsign over RHPv2; serves its own web UI
service:
  command: ./pdn-convers
  environment: {}              # PDN_APP_STATE / PDN_RHP_HOST / PDN_RHP_PORT from the supervisor
ui:
  upstream: http://127.0.0.1:18091   # MUST match web.port in convers.yaml
  name: Convers
  icon: chat
```

## Open decisions for Tom

1. **Uplink transport (decision 6)** — is packet.net's convers uplink an **RF neighbour** (RHP open, pdn-native) or an **internet hub** (direct TCP, needs 44Net/allowlist), or both? This is the only fork that affects deployment. Who's the intended parent node?
2. **Inbound downstream peering** — v1 leaf-only (users in, one uplink out) is simplest and safest. Agreed to defer accepting downstream peers to W7?
3. **Channel policy** — a fixed default channel for packet.net users, or free choice? Any local-only channels (conversd `+l`) you want reserved?
```
