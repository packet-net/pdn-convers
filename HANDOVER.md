# pdn-convers — handover package

> **Status (2026-06-13): the build is well underway.** Waves **W0–W6 are merged** and the node works
> **end-to-end** — an RF/web user chats on a real convers channel through the uplink, a network user's
> message comes back, and everything is logged; proven live against the conversd oracle and CI-enforced
> both directions. **W7 (the optional SHOULD wave) is in progress.** This file began as the from-zero
> bootstrap; it is now the project's orientation + status doc. Live wave-by-wave state lives in the
> memory `pdn-convers-status.md`; the architecture is `docs/design.md`.

**What it is:** `m0lte/pdn-convers` — a ground-up packet-radio convers (round-table chat) node for the
pdn platform, built in the vein of `m0lte/pdn-bbs`. Authored 2026-06-12 for Tom M0LTE.

> Read order: this file → `docs/design.md` (architecture + build-wave status) → `reference/SPECS.txt`
> (the convers host-protocol spec). The C reference implementation is vendored under
> `reference/conversd-saupp/` (and built as the docker oracle under `docker/`).

---

## 1. What this is

Give pdn's RF users (and the node owner via a web tile) a first-class interface to the worldwide **convers network** ("Tampa PingPong" / WW Convers; the `conversd-saupp` lineage). pdn-convers joins that network as a **single leaf node** over one upstream host link and presents every local pdn user as an authenticated convers user. Built in the exact vein of **`m0lte/pdn-bbs`** (the template repo): C#/.NET, strictly layered, app-package-only (talks to the node solely over RHPv2), plain-language UX, SQLite state, `.deb` packaging, oracle-first testing.

Full design + rationale: **`docs/design.md`** (architecture, 10 load-bearing decisions, W0–W7 build waves, release pipeline, `pdn-app.yaml` sketch).

## 2. Settled decisions (Tom, 2026-06-12)

| # | Decision | Consequence |
|---|---|---|
| Uplink transport | **No parent/peer node secured yet.** Build *both* providers behind one `IUpstreamLink` (RF-via-RHP-`open`, and direct-TCP-to-hub); leave the default unset until a parent exists. | The live network is **blocked on an external prerequisite** (§4). Develop/test against our own conversd in docker as the stand-in parent. |
| Downstream peering | **v1 is leaf-only** (users in, one uplink out). Accepting downstream peers is **deferred to W7**. | InboundDemux treats every inbound RF connect as a USER; no inbound `/..HOST` handling in v1. Simplifies the demux (design decision 3). |
| Default channel | **Fixed default channel** for packet.net users — a `defaultChannel` config key. | Home channel **2723** (Tom's pick, 2026-06-13; a random number in 256–32767). Users land there on connect without choosing. |

These three are now the "Resolved decisions" section at the bottom of `docs/design.md`.

## 3. Current status (2026-06-13)

- **W0–W6 merged.** The four-project solution (`Convers.Protocol`/`Core`/`Console`/`Host`) is built on `main`: the wire codec + USER/HOST command sets + connection FSM (W1), the `ConversHub` presence model + SQLite persistence incl. a full chat log (W2 + chat-logging), the plain-language + classic console (W3), the upstream HostLink (W4), the RHP client + inbound demux + RF/TCP uplink providers + SSID probe-walk + the web chat tile (W5a/W5b), and the conversd-oracle both-directions interop suite (W6). **443 unit tests + 6 interop tests, 0 warnings;** both CI lanes green on the self-hosted runner.
- **Proven working.** An RF/web user chats on a real convers channel through the uplink, a network user's message comes back, and everything (messages + presence, local + network origin) is logged — demonstrated live against the conversd docker oracle and CI-enforced both directions.
- **W7 (optional SHOULD wave) in progress** — channel modes, `/..OPER`, away, topic propagation, `/..ROUT`/`/..SYSI`, link-time `p`, compression, downstream peering, classic niceties (§6).
- **Reference source: vendored** under `reference/conversd-saupp/` (the DL9SAU `conversd-saupp` CVS export; canonical online copy `http://x-berg.in-berlin.de/cgi-bin/viewcvs.cgi/ampr/conversd-saupp/` `?view=tar`), and built as the docker oracle under `docker/`.

## 4. External prerequisite — BLOCKING for live network (Tom to action)

There is **no parent convers node yet**, so pdn-convers cannot join the real network until one is arranged. This is a people problem, not a code problem, and is independent of the build:

- **Option A — internet hub:** email a hub op to get packet.net's public IP/FQDN allowlisted as `Access … HOST` (e.g. N2NOV's HubNA `44.68.41.2:3600` for NA; find/confirm a UK/EU hub — RRRWWC / PE1RRR's `Rijen_NL` appeared in logs). A 44Net source address may skip the allowlist step.
- **Option B — RF neighbour:** find a nearby station already running a convers node and link to it over RF via RHP `open` (the pdn-native path).

The build does **not** wait on this — W0–W6 develop and test against a local conversd container. But the **default uplink provider** and the W6 *live* demo do. Track this as the one open real-world dependency.

## 5. Key technical reference (so the fresh context needn't re-fetch)

**The convers protocol** — full spec is `reference/SPECS.txt` (verbatim `doc/SPECS`). Essentials:
- Transport: line-based, CR/LF tolerant, Latin-1. Default TCP port **3600** (wconvers 3610, lconvers 6810).
- Connection states: `UNKNOWN → USER | OBSERVER | HOST`.
- **USER login:** `/NAME <call> [channel]` (we auto-issue this from the RHP-authenticated callsign; users never type it). `/n` is the accepted abbreviation seen in the wild.
- **HOST handshake:** `/..HOST <hostname≤9ch> [software [facilities]]`. Facility letters: `a`/`A` away old/new, `d` dest-forwarding, `m` channel-modes, `p` ping-pong link timing, `u` udat/user ext, `n` TNOS nicks.
- **HOST commands** (all `/..`-prefixed). NECESSARY: `/..USER <user> <host> <ts> <fromchan> <tochan> [@|text]` (presence; tochan −1 = signoff), `/..CMSG <user> <chan> <text>` (channel msg; `user=conversd` ⇒ broadcast, no formatting), `/..UMSG <from> <to> <text>` (PM), `/..UDAT <user> <host> [text]` (personal text), `/..INVI <from> <user> <chan>`. Plus `/..PING` / `/..PONG <time>` keepalive. OPTIONAL: `TOPI MODE OPER AWAY OBSV DEST ROUT SYSI LOOP ECMD HELP UADD`.
- **Golden rule:** unrecognised `/..` commands are relayed unmodified to all *other* connected hosts. **For a strict leaf this is a no-op** (one uplink = no other host) — see design decision 1.
- **Peering/ACL model** (`reference/conversd-saupp/etc/conversd.conf`): `Link <host> <addr:port> [primary]` (ONE primary; the net is a TREE, no loops); `Access <addr[/mask]> <BAN|USER|HOST|HOST_SECURE|OBSERVER|RESTRICTED|AUTH>`; host links may require a password (`host.c check_password`). `conversd-saupp` persists personal text / nicks / topics — the differentiators we replicate in SQLite.

**Reference files worth reading in `reference/conversd-saupp/`:** `doc/SPECS` (protocol), `user.c` (USER grammar), `host.c` (the peer state machine — the model to port), `convers.c` (core), `etc/conversd.conf` (config/ACL/Link), `etc/convers.help` (the command surface), `Makefile` + `etc/*.rc.d` (to build it in docker as the oracle).

**pdn / RHPv2 integration facts (from the pdn-bbs source — clone its conventions):**
- RHP client lib: `RhpV2.Client` (`RhpClient.ConnectAsync` → optional `AuthenticateAsync` → `SocketAsync(ProtocolFamily.Ax25, SocketMode.Stream)` → `BindAsync(listener, callsign, port:null)` (null = all ports) → `ListenAsync(OpenFlags.Passive)`; events `Received`/`Accepted`/`Closed`/`Disconnected`; outbound via `open`/Active). Resilient reconnect with exponential backoff, re-bind on every reconnect, fault all children on link loss. See pdn-bbs `src/Bbs.Host/Rhp/RhpNodeLink.cs` + `RhpChildConnection.cs` — these are directly reusable patterns.
- Session seam: an `IBbsTerminal`-equivalent (`IConverseTerminal`) — sans-IO, Latin-1, line-at-a-time, CR discipline. See pdn-bbs `IBbsTerminal.cs` + `RhpTerminal.cs` + `LineAssembler.cs`.
- Composition: `HostComposition.Build(args)` returns a `WebApplication`; component loops registered as `ComponentService<T>` hosted services (closed generics so each is distinct — a known footgun, pinned by `HostCompositionTests`). State dir from `PDN_APP_STATE` (fallback cwd); RHP endpoint from `PDN_RHP_HOST`/`PDN_RHP_PORT`; webmail binds the loopback port the manifest names.
- Manifest: `pdn-app.yaml` — `manifest: 1`, `id: convers` (== package dir name), `capabilities: [network, web]`, `service.command: ./pdn-convers`, `ui.upstream: http://127.0.0.1:<port>` (MUST match `web.port` in `convers.yaml`). Sketch in `design.md`.
- Packaging: per-arch `.deb` (amd64/arm64/armhf) under `/usr/share/packetnet/apps/convers`; state in `/var/lib/packetnet/apps/convers`; `publish-convers` workflow + `scripts/deploy-convers.sh` — clone from pdn-bbs `packaging/`, `scripts/`, `.github/workflows/`, `docs/release-pipeline.md`.

## 6. Remaining work & how to continue

The core build (W0–W6) is done. What's left:

- **W7 — the optional SHOULD wave (in progress).** Channel modes (`/..MODE` +i/+l/+m/+p/+s/+t), `/..OPER`, away (`/..AWAY` new+old), `/..TOPI` persistence+propagation, `/..ROUT`/`/..SYSI`, link-time `p` measurement, compression/`u` extensions, the **downstream-peering toggle** (accept inbound HOST links — the deferred decision-4 feature), and classic-client niceties. Done in slices: **W7a** Core semantics (modes/away/topic) → **W7b** Host (oper/rout/sysi/p/compression/peering) + **W7c** Console surface.
- **Real-world steps (Tom to action).** The lab `.deb` deploy (`scripts/deploy-convers.sh` → `root@packetdotnet`), and arranging a **parent convers node** so pdn-convers can join the live public network — still the one external prerequisite (§4).

**To pick up the build:** read this file → `docs/design.md` (architecture + build-wave status) → the memory `pdn-convers-status.md` (live wave-by-wave state). Waves run as sub-agents off `main`, each a PR; **verify before merging** — `dotnet build -c Release` 0-warning, the unit lane (`--filter "Category!=Interop"`) green and stable under a flake-stress (the Host suite has socket/timing tests; pass an explicit state dir, never the process-global `PDN_APP_STATE`, and use poll-advance not a bare `Time.Advance`), and the interop lane (`--filter "Category=Interop"`) green against the docker oracle. CI runs both lanes on the self-hosted runner (RAM-capped 8 GB — keep parallelism modest).

## 7. Memory pointers (persist across contexts)

- `pdn-convers-status.md` — the live wave-by-wave build status + what's settled/remaining.
- `convers-callsign-autoderive.md` — the `<node-callsign>-<ssid>` auto-derivation (default SSID 4) + the SSID probe-walk.
- `convers-chat-logging.md` — the full chat-log decision (channels + PMs + presence, kept forever).
- `convers-oracle-docker.md` — docker availability + the verified conversd oracle.
- `pdn-bbs-template.md` — `m0lte/pdn-bbs` is the canonical template for every pdn-app convention (RAM-capped 8 GB CI runner; clone it for reference patterns).

## 8. Suggested first message for a fresh context

> "Pick up the pdn-convers build. Read `HANDOVER.md`, then the memory `pdn-convers-status.md` and `docs/design.md`. W0–W6 are merged and the node works end-to-end both directions (oracle-proven); W7 (the optional SHOULD wave) is in progress — continue it in slices (W7a Core → W7b Host + W7c Console), each a verified PR off `main`."
