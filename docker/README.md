# conversd-saupp oracle (`compose.oracle.yml`)

A docker compose stack that builds the vendored **DL9SAU `conversd`** (`reference/conversd-saupp/`)
from source and runs **one instance** as the stand-in **upstream parent node** for pdn-convers'
interop lane. The convers network is a tree and a leaf links to exactly one parent (design.md
decision 1), so a single conversd is the whole oracle — far simpler than pdn-bbs's LinBPQ+netsim
stack.

It is stood up early (HANDOVER.md §6 step 5) so W4's HostLink can develop against a real conversd
wire **before any live parent node exists** (the one real-world dependency — HANDOVER.md §4).

| Service | Container | Role |
|---|---|---|
| `conversd` | `pdnconvers-conversd` | conversd-saupp 1.62a built from the vendored source. Listens on TCP 3600 (+ a unix socket). pdn-convers links in as a `HOST`; interop tests connect as `USER`s. Default channel 3333. |

## Running it

```sh
docker compose -f docker/compose.oracle.yml up -d --build --wait   # build, bring up, gate on healthcheck
docker compose -f docker/compose.oracle.yml logs -f                # watch conversd
docker compose -f docker/compose.oracle.yml down -v                # tear down
```

`--wait` blocks until the healthcheck (a bash `/dev/tcp` probe of port 3600) passes, i.e. conversd
is accepting connections.

## Port map (loopback-only on the host)

| Host | Container | What |
|---|---|---|
| `127.0.0.1:3600` | conversd `:3600` | the convers TCP port — our side of the upstream host link, and where interop tests connect |

## How it is built

`docker/Dockerfile.conversd` (build context = repo root) compiles only the `conversd` server target
(`make conversd`) on `debian:bookworm-slim` — no libreadline/ncurses needed (those are only for the
interactive `convers` client). conversd derives its config filenames from the binary name, so the
image lays the config out where it looks for it:

```
/usr/local/etc/conversd/conversd.conf    (docker/oracle/conversd.conf — oracle-tuned)
/usr/local/etc/conversd/conversd.issue   (docker/oracle/convers.issue)
/usr/local/etc/conversd/conversd.motd    (docker/oracle/convers.motd)
/usr/local/etc/conversd/convers.help     (the canonical help text from the vendored source)
/var/cache/conversd/…                     (state: pidfile, logs, the unix socket, persisted
                                           personals/nicknames/topics — owned by `daemon`)
```

conversd runs as `conversd -f` (foreground, so it is the container's main process) and drops its
effective uid to `daemon` at startup (hence the daemon-owned state tree; the container starts as
root so the `seteuid()` succeeds).

## The oracle config (`docker/oracle/conversd.conf`)

A throwaway, wide-open conversd: `Access 0.0.0.0/0 USER HOST` (it is only reachable on the
loopback-published host port), `CallValidate 0` so interop tests can `/name` with arbitrary test
calls, `DefaultChan 3333` to match pdn-convers' placeholder default, sysop login disabled. **Do not
copy this onto the live network.**

## What `Convers.Interop.Tests` uses it for

W0's lane (`ConversOracleSmokeTests`) proves the stack is up and the convers port accepts
connections — the precondition for everything later. As the waves land, the lane grows to drive the
**composed host** against this conversd both directions (the diff-oracle discipline — design.md
decision 10): an RF/web user's message reaches a convers channel observed on the real conversd, and a
network user's message reaches the local session. CI's `interop` job (`.github/workflows/ci.yml`)
brings the oracle up with `--build --wait`, runs `--filter "Category=Interop"`, then tears it down.

## Manual wire check

```sh
docker compose -f docker/compose.oracle.yml up -d --build --wait
printf '/name G0TST 3333\r\n/who\r\n' | nc -q1 127.0.0.1 3600   # issue, banner, channel 3333, /who
```

(Verified 2026-06-12: conversd emits its issue on connect, accepts `/name`, creates channel 3333 and
answers `/who` — a genuinely wire-live oracle.)
