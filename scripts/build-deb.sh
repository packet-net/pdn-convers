#!/usr/bin/env bash
#
# build-deb.sh — publish pdn-convers (the convers app package) self-contained for one RID and
# package it as a Debian .deb. Used locally and by publish-convers.yml.
#
#   scripts/build-deb.sh <rid> <version>
#   e.g. scripts/build-deb.sh linux-arm64 0.1.0
#
# Cross-publishes from x64 (plain self-contained, NO ReadyToRun/crossgen2 — it OOMs on cross-publish
# on the RAM-capped runner and the cold-start gain is marginal), so all three arches build on the one
# self-hosted x64 runner with no arch-native machine or cross C-toolchain. Produces
# artifacts/pdn-convers_<version>_<arch>.deb.
#
# The .deb carries exactly the CODE:
#   usr/share/packetnet/apps/convers/pdn-convers     (the self-contained single-file binary)
#   usr/share/packetnet/apps/convers/pdn-app.yaml    (the app manifest, from repo root, version stamped)
# Runtime STATE (convers.db, convers.yaml) lives in /var/lib/packetnet/apps/convers and is NEVER
# shipped — see docs/release-pipeline.md for the code-vs-state split.
set -euo pipefail

rid="${1:?usage: build-deb.sh <rid> <version>}"
version="${2:?usage: build-deb.sh <rid> <version>}"

case "$rid" in
  linux-x64)   arch=amd64 ;;
  linux-arm64) arch=arm64 ;;
  linux-arm)   arch=armhf ;;
  *) echo "unknown rid: $rid (want linux-x64 | linux-arm64 | linux-arm)" >&2; exit 2 ;;
esac

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
proj="$root/src/Convers.Host/Convers.Host.csproj"
pub="$root/artifacts/convers/$rid"
stage="$root/artifacts/deb/$rid"
out="$root/artifacts/pdn-convers_${version}_${arch}.deb"

# CRITICAL flags (learned the hard way in pdn-bbs):
#   PublishSingleFile=true                    — one self-extracting binary named pdn-convers
#   IncludeNativeLibrariesForSelfExtract=true — bundle e_sqlite3 (Convers.Core's saupp persistence)
#       INTO that single file; WITHOUT this the native sqlite lib is left loose and the app crashes
#       on startup with DllNotFoundException: e_sqlite3.
#   InvariantGlobalization=true               — no ICU dependency
#   DebugType=none / DebugSymbols=false        — no .pdb in the package
# NO PublishReadyToRun/crossgen2: it OOMs on cross-publish and the cold-start win is marginal.
#
# PDN_FAST=1: the dev-loop path (scripts/deploy-convers.sh). It skips NOTHING critical — single-file
# + IncludeNativeLibrariesForSelfExtract stay ON. It exists as a named seam for parity with the node
# pipeline; today fast == release flags.
publish_flags=(
  -p:PublishSingleFile=true
  -p:IncludeNativeLibrariesForSelfExtract=true
  -p:InvariantGlobalization=true
  -p:DebugType=none
  -p:DebugSymbols=false
)
if [ "${PDN_FAST:-}" = "1" ]; then
  echo "==> publish $rid (PDN_FAST dev loop: self-contained, single-file, sqlite bundled)"
else
  echo "==> publish $rid (release: self-contained, single-file, sqlite bundled)"
fi
dotnet publish "$proj" -c Release -r "$rid" --self-contained true \
  -p:Version="$version" \
  "${publish_flags[@]}" \
  -v minimal -o "$pub"

bin="$pub/pdn-convers"
[ -f "$bin" ] || { echo "ERROR: expected single-file binary $bin but it wasn't produced" >&2; exit 1; }

echo "==> stage .deb tree for $arch"
rm -rf "$stage"
install -d "$stage/usr/share/packetnet/apps/convers" "$stage/DEBIAN"
# CODE only: the single-file binary (0755) + the app manifest (0644). NEVER ship
# convers.yaml / convers.db (runtime state — lives in /var/lib/packetnet/apps/convers).
install -m 0755 "$bin" "$stage/usr/share/packetnet/apps/convers/pdn-convers"
# The manifest's `version:` is what pdn's installed-apps list shows, so stamp it from the build
# version (the tag, for a release) rather than trusting the hand-edited value in the repo copy.
# v0.1.4 shipped a manifest still saying 0.1.3 because nothing tied the two together.
manifest="$stage/usr/share/packetnet/apps/convers/pdn-app.yaml"
[ "$(grep -c '^version:' "$root/pdn-app.yaml")" = "1" ] || {
  echo "ERROR: pdn-app.yaml must have exactly one top-level version: line" >&2; exit 1; }
sed -e "s/^version:.*/version: \"$version\"/" "$root/pdn-app.yaml" > "$manifest"
chmod 0644 "$manifest"

sed -e "s/@ARCH@/$arch/" -e "s/@VERSION@/$version/" \
    "$root/packaging/control.in" > "$stage/DEBIAN/control"
cp "$root/packaging/postinst" "$stage/DEBIAN/postinst"
chmod 0755 "$stage/DEBIAN/postinst"

echo "==> build .deb"
mkdir -p "$root/artifacts"
# --root-owner-group (dpkg >= 1.19): root:root files without fakeroot.
# -Zxz: pin xz - dpkg-deb's zstd default (dpkg >= 1.21.18) can't be unpacked by
# Debian Bullseye's dpkg, so a zstd .deb refuses to install there.
dpkg-deb --build --root-owner-group -Zxz "$stage" "$out"

# Read the manifest back out of the built package: the version pdn will display must be this build's.
packaged="$(dpkg-deb --fsys-tarfile "$out" | tar -xO ./usr/share/packetnet/apps/convers/pdn-app.yaml | grep '^version:')"
if [ "$packaged" != "version: \"$version\"" ]; then
  echo "ERROR: packaged pdn-app.yaml says '$packaged', expected version \"$version\"" >&2
  exit 1
fi

echo "==> built $out"
dpkg-deb --info "$out"
echo "--- contents ---"
dpkg-deb --contents "$out" | awk '{print $1, $6}'
if command -v lintian >/dev/null 2>&1; then
  lintian "$out" || true
else
  echo "(lintian not installed — skipping deb-lint)"
fi
