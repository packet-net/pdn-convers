# Release + deploy pipeline

pdn-convers ships as a versioned Debian `.deb`, exactly like pdn-bbs and the packet.net node host
— no hand-staging the binary. This note documents the pipeline and, crucially, the **code vs state**
split that makes it safe to reinstall over a live convers node.

## The artifact

`pdn-convers` is a .NET (`net10.0`) app. `dotnet publish` produces a self-contained single-file
binary named `pdn-convers` (the `Convers.Host` project, `AssemblyName=pdn-convers`). The `.deb`
carries exactly the **code**:

```
usr/share/packetnet/apps/convers/pdn-convers     # the self-contained single-file binary (0755)
usr/share/packetnet/apps/convers/pdn-app.yaml    # the app manifest, copied from the repo root (0644)
```

That is all. No config, no database, no systemd unit (the app is supervised by the packetnet node,
not by systemd directly).

### Publish flags (and why)

```
-r <rid> --self-contained true
-p:PublishSingleFile=true
-p:IncludeNativeLibrariesForSelfExtract=true
-p:InvariantGlobalization=true
-p:DebugType=none -p:DebugSymbols=false
```

- **`IncludeNativeLibrariesForSelfExtract=true` is not optional.** `Convers.Core` persists its
  saupp differentiators (personal text, nicks, passwords, topics) with `Microsoft.Data.Sqlite`,
  which carries the native `e_sqlite3` library. With single-file publish but *without* this flag
  that native lib is left loose next to the binary instead of bundled into it — so a `.deb` that
  ships only `pdn-convers` crashes on startup with `DllNotFoundException: e_sqlite3`. (Learned the
  hard way in pdn-bbs.)
- **No `PublishReadyToRun` / crossgen2.** R2R OOMs on cross-publish (the arm64 cross-publish is the
  heaviest, and the self-hosted runner is RAM-capped at 8 GB) and the cold-start gain is marginal.
  Plain self-contained cross-publish from x64 is fine for all three arches, so the whole release
  builds on one self-hosted x64 runner with no arch-native machines and no cross C-toolchain.

## Code vs state: `/usr/share` vs `/var/lib`

The packet.net node discovers app packages by scanning **two** roots:

| Root | Owner | What lives there |
|------|-------|------------------|
| `/usr/share/packetnet/apps` | the package (root, read-only to the service) | **code** — `pdn-convers` + `pdn-app.yaml` |
| `/var/lib/packetnet/apps`   | the `packetnet` service user (0750) | **state** — `convers.db`, `convers.yaml`, `*.db-wal`/`*.db-shm` |

Each app's state dir is always `/var/lib/packetnet/apps/<id>` regardless of where its code was
discovered. So for convers:

- The `.deb` installs **code** to `/usr/share/packetnet/apps/convers/`.
- The app writes its **state** to `/var/lib/packetnet/apps/convers/` (a commented default
  `convers.yaml` on first run; `convers.db` as users set personal text / topics).

The `.deb` **never** ships `convers.yaml` or `convers.db`, and they are **not** conffiles (runtime
state, not packager-owned config). On upgrade, dpkg replaces only the code under `/usr/share`; the
state under `/var/lib` is untouched, so the operator's config and persisted convers state survive.

The `postinst` only prepares the state dir (`install -d -o packetnet -g packetnet -m 0750
/var/lib/packetnet/apps/convers`) **if the `packetnet` user exists** — the node host package owns
that user, and convers is a soft `Recommends: packetnet`, so on a standalone install the user may be
absent. pdn creates the per-app state dir at runtime anyway, so an absent dir here is harmless. The
`postinst` is idempotent and deliberately does **not** restart the packetnet service.

## Don't leave hand-staged code under `/var/lib` (later-root-wins)

When the node finds the same app `id` under both roots, **the later root wins** — `/var/lib`
overrides `/usr/share`. That's the right rule for an owner overriding a bundled app, but a trap if a
box still carries an old hand-staged copy under `/var/lib`: fresh code under `/usr/share` is then
shadowed by the stale `/var/lib` copy forever. Neither the `.deb` nor `scripts/deploy-convers.sh`
touches `/var/lib` — they only install code under `/usr/share`, and `/var/lib` is left entirely to
state. Start every box `/usr/share`-only.

## The two entry points

### Release — `.github/workflows/publish-convers.yml`

Triggered by a `v*` tag (or a manual `workflow_dispatch` with an explicit version). Runs on
`[self-hosted, Linux, X64]` (no GitHub-hosted runners — this repo has no hosted-runner budget).
Resolves the version from the tag (`${GITHUB_REF#refs/tags/v}`) or the dispatch input, loops the
three RIDs through `scripts/build-deb.sh`, `sha256sum`s the three `.deb`s into `SHA256SUMS`, and
`gh release create`s the tag with the three `.deb`s + `SHA256SUMS`.

```
git tag v0.1.0 && git push origin v0.1.0      # → builds amd64/arm64/armhf, cuts the release
```

### Dev loop — `scripts/deploy-convers.sh`

The tight build → deploy → show loop against the live lab box (`root@packetdotnet`), no CI wait,
same artifact shape GHA ships:

```
scripts/deploy-convers.sh            # build amd64 (PDN_FAST=1), scp, dpkg -i, restart, verify
scripts/deploy-convers.sh --logs     # …then follow the journal
scripts/deploy-convers.sh --skip-build   # redeploy the most recent existing .deb
```

It builds with `PDN_FAST=1`, the named dev-loop seam — it skips nothing critical (single-file + the
bundled sqlite lib stay on, or the app won't start). After install it restarts the packetnet node
and prints a liveness summary: service active, `/healthz`, the convers app starting in the journal.

## Manual one-arch build

```
scripts/build-deb.sh linux-x64 0.1.0           # → artifacts/pdn-convers_0.1.0_amd64.deb
scripts/build-deb.sh linux-arm64 0.1.0         # → artifacts/pdn-convers_0.1.0_arm64.deb
PDN_FAST=1 scripts/build-deb.sh linux-x64 ...  # dev-loop flags (same critical flags today)
```

RID → arch map: `linux-x64`→`amd64`, `linux-arm64`→`arm64`, `linux-arm`→`armhf`.
