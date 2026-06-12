# pdn-convers — handover package

**Purpose:** everything a fresh context needs to start building `m0lte/pdn-convers` — a ground-up packet-radio convers (round-table chat) node for the pdn platform — without re-deriving the research. Authored 2026-06-12 for Tom M0LTE.

> Read order: this file → `design.md` (the full architecture + build waves) → `reference/SPECS.txt` (the convers host-protocol spec). The C reference implementation is vendored under `reference/conversd-saupp/`.

---

## 1. What this is

Give pdn's RF users (and the node owner via a web tile) a first-class interface to the worldwide **convers network** ("Tampa PingPong" / WW Convers; the `conversd-saupp` lineage). pdn-convers joins that network as a **single leaf node** over one upstream host link and presents every local pdn user as an authenticated convers user. Built in the exact vein of **`m0lte/pdn-bbs`** (the template repo): C#/.NET, strictly layered, app-package-only (talks to the node solely over RHPv2), plain-language UX, SQLite state, `.deb` packaging, oracle-first testing.

Full design + rationale: **`design.md`** in this folder (mirror of pdn-bbs's `docs/design.md` style — architecture, 10 load-bearing decisions, W0–W7 build waves, release pipeline, `pdn-app.yaml` sketch).

## 2. Settled decisions (Tom, 2026-06-12)

| # | Decision | Consequence |
|---|---|---|
| Uplink transport | **No parent/peer node secured yet.** Build *both* providers behind one `IUpstreamLink` (RF-via-RHP-`open`, and direct-TCP-to-hub); leave the default unset until a parent exists. | The live network is **blocked on an external prerequisite** (§4). Develop/test against our own conversd in docker as the stand-in parent. |
| Downstream peering | **v1 is leaf-only** (users in, one uplink out). Accepting downstream peers is **deferred to W7**. | InboundDemux treats every inbound RF connect as a USER; no inbound `/..HOST` handling in v1. Simplifies the demux (design decision 3). |
| Default channel | **Fixed default channel** for packet.net users — a `defaultChannel` config key. | Scaffold ships placeholder `3333`; Tom picks the real public number (256–32767, avoid collisions) before go-live. Users land there on connect without choosing. |

These resolve the three "Open decisions for Tom" at the bottom of `design.md`.

## 3. Current status

- **Research: complete.** Protocol, ports, hubs, peering model, canonical source all identified and pinned (see memory `convers-integration.md`).
- **Design: complete.** `design.md` is ready to become the new repo's `docs/design.md`.
- **Reference source: vendored** under `reference/conversd-saupp/` (74 files; the DL9SAU `conversd-saupp` CVS export). Canonical online copy: `http://x-berg.in-berlin.de/cgi-bin/viewcvs.cgi/ampr/conversd-saupp/` (`?view=tar`).
- **Code: not started.** Next action is **W0 scaffold** (§6).

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

## 6. First session: W0 scaffold (start here)

1. `gh repo create m0lte/pdn-convers` (private/public per Tom). Add `docs/design.md` (this folder's `design.md`), and vendor `reference/conversd-saupp/` into the repo (e.g. `docs/reference/` or a `third_party/` dir) so the oracle + spec travel with the code. Commit `reference/SPECS.txt`.
2. Clone the pdn-bbs skeleton conventions: `Directory.Build.props`, `Directory.Packages.props` (CPM), `.gitignore`, `pdn-convers.slnx`, `.github/workflows/ci.yml`, `packaging/`, `scripts/build-deb.sh` + `deploy-convers.sh`, `docs/release-pipeline.md`. Adapt ids `bbs`→`convers`.
3. Create the four empty projects + test projects per `design.md` (`Convers.Protocol`, `Convers.Core`, `Convers.Console`, `Convers.Host` + `tests/*`). Enforce the dependency rule.
4. Author `pdn-app.yaml` (sketch in `design.md`) and the first-run default `convers.yaml` (callsign `N0CALL`, `defaultChannel: 3333`, uplink block with both providers commented, sysop, web port).
5. `docker/` oracle compose: build `conversd-saupp` from the vendored source, run one instance as the stand-in parent. (This is the W6 oracle, stood up early so W4 can test against it.)
6. CI green on the self-hosted runner (packet.net conventions — note runner is RAM-capped 8 GB per `github-actions-runner-infra` memory; keep build parallelism modest).
7. Empty-green test lanes per project. Tag nothing yet.

Then **W1** (`Convers.Protocol`) and **W2** (`Convers.Core`) can run in parallel as sub-agents — the spec + vendored source carry everything. W3 follows once Core interfaces pin. See `design.md` "Build waves" for the full W0–W7 sequence and dependencies.

## 7. Memory pointers (persist across contexts)

- `convers-integration.md` — protocol/ports/hubs/peering, the conversd-saupp source location, and the decision record. Updated 2026-06-12 with the settled decisions + this handover's location.
- `github-actions-runner-infra.md` — the CI runner is RAM-constrained (8 GB cap); relevant to W0 CI setup.
- pdn-bbs itself (`m0lte/pdn-bbs`) is the canonical template for every pdn-app convention referenced here.

## 8. Suggested first message for the fresh context

> "Pick up the pdn-convers build. Read `C:\Users\tom\pdn-convers-handover\HANDOVER.md` then `design.md` in that folder. All three open decisions are settled (leaf-only v1, fixed default channel 3333 placeholder, no parent node yet — build both uplink providers). Start W0 scaffold: create the `m0lte/pdn-convers` repo, clone the pdn-bbs conventions, stand up the four projects + the conversd-saupp docker oracle. Then hand W1/W2 to sub-agents."
