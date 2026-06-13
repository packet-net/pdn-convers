# pdn-convers

A ground-up packet-radio **convers (round-table chat) node** for the [pdn](https://github.com/M0LTE/packet.net) node platform — giving pdn's RF users (and the node owner via a web tile) a first-class interface to the worldwide convers network ("Tampa PingPong" / WW Convers; the `conversd-saupp` lineage).

Built in the vein of [`m0lte/pdn-bbs`](https://github.com/m0lte/pdn-bbs): strictly an **app package** that reaches the node only through public interfaces (RHPv2, the app-gateway web identity contract, a `pdn-app.yaml` manifest). pdn contains zero convers-specific code.

> **Status: W0–W6 complete (W7 in progress).** The full node is built on `main` — protocol, domain
> (+ a full chat log), console, host, RF + web tile, and the conversd-oracle both-directions interop
> suite — and it works **end-to-end**: an RF/web user chats on a real convers channel through the
> uplink, a network user's reply comes back, all logged (oracle-proven, CI-enforced). 443 unit + 6
> interop tests, 0 warnings. W7 (the optional SHOULD wave) is underway. Start with
> **[`HANDOVER.md`](HANDOVER.md)**, then [`docs/design.md`](docs/design.md).

## Layout

```
src/Convers.Protocol   the wire (sans-IO): the '/'-grammar codec, USER + HOST command sets, the
                       UNKNOWN→USER|OBSERVER|HOST connection FSM. Parse/emit only — no sockets.
src/Convers.Core       domain: the in-memory ConversHub (channels, presence, the network user/dest
                       table) + SQLite persistence (personal text, nicks, passwords, topics).
src/Convers.Console    the terse RF user surface — plain-language chat by default, per-user `classic`
                       mode for the literal conversd '/'-commands.
src/Convers.Host       composition: RHPv2 client, inbound demux, the single upstream host link,
                       ASP.NET web chat, YAML config, the pdn-app.yaml manifest.
tests/                 unit per project + tests/Convers.Interop.Tests (the conversd-saupp oracle lane).
docker/                the oracle: conversd-saupp built from the vendored source as the stand-in parent.
```

Dependency rule (enforced by the project references): `Protocol` and `Core` are independent and
never reference `Host`; `Console` references `Core` only; `Host` references all three.

## Building

```sh
dotnet build -c Release
dotnet test  -c Release --filter "Category!=Interop"   # unit lanes
```

The interop lane (`Category=Interop`) runs against the conversd oracle — bring it up first:

```sh
docker compose -f docker/compose.oracle.yml up -d --wait
dotnet test -c Release --filter "Category=Interop"
```

Packaging and release: [`docs/release-pipeline.md`](docs/release-pipeline.md)
(`scripts/build-deb.sh`, `scripts/deploy-convers.sh`, the `publish-convers` workflow).

## Contents

- [`HANDOVER.md`](HANDOVER.md) — front door: settled decisions, status, the external prerequisite (no parent node yet), key technical reference, and the W0-scaffold first-session checklist.
- [`docs/design.md`](docs/design.md) — full architecture: four-project layering, 10 load-bearing decisions, W0–W7 build waves, release pipeline, `pdn-app.yaml` sketch.
- [`docs/release-pipeline.md`](docs/release-pipeline.md) — the `.deb` release + deploy pipeline and the code-vs-state split.
- [`reference/SPECS.txt`](reference/SPECS.txt) — the convers host-protocol spec (verbatim `conversd-saupp/doc/SPECS`).
- [`reference/conversd-saupp/`](reference/conversd-saupp/) — the vendored canonical C reference implementation (DL9SAU `conversd-saupp`). Doubles as the docker interop oracle / stand-in parent node.
- [`docker/README.md`](docker/README.md) — the conversd oracle stack (build, run, smoke).
