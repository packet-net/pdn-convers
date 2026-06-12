# pdn-convers

A ground-up packet-radio **convers (round-table chat) node** for the [pdn](https://github.com/M0LTE/packet.net) node platform — giving pdn's RF users (and the node owner via a web tile) a first-class interface to the worldwide convers network ("Tampa PingPong" / WW Convers; the `conversd-saupp` lineage).

Built in the vein of [`m0lte/pdn-bbs`](https://github.com/m0lte/pdn-bbs): strictly an **app package** that reaches the node only through public interfaces (RHPv2, the app-gateway web identity contract, a `pdn-app.yaml` manifest). pdn contains zero convers-specific code.

> **Status: pre-code.** This repo currently holds the handover/seed package. Start with **[`HANDOVER.md`](HANDOVER.md)**, then [`design.md`](design.md).

## Contents

- [`HANDOVER.md`](HANDOVER.md) — front door: settled decisions, status, the external prerequisite (no parent node yet), key technical reference, and the W0-scaffold first-session checklist.
- [`design.md`](design.md) — full architecture: four-project layering, 10 load-bearing decisions, W0–W7 build waves, release pipeline, `pdn-app.yaml` sketch.
- [`reference/SPECS.txt`](reference/SPECS.txt) — the convers host-protocol spec (verbatim `conversd-saupp/doc/SPECS`).
- [`reference/conversd-saupp/`](reference/conversd-saupp/) — the vendored canonical C reference implementation (DL9SAU `conversd-saupp`). Doubles as the docker interop oracle / stand-in parent node.
