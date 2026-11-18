#!/usr/bin/env bash
# End-to-end test of `mysync update` and install.sh with real published binaries (CLAUDE.md §16).
#
# Publishes mysync 9.9.8 and 9.9.9 for one runtime the way the release workflow does: both trust a
# throwaway test key and name a release repository, baked in at build time (-p:MySyncReleasePublicKey,
# -p:MySyncReleaseRepository; the workflow passes its own key and repository). Lays 9.9.9 out as a
# signed release folder, installs 9.9.8 somewhere writable, and checks that:
#   - without MYSYNC_UPDATE_SOURCE the binary asks the repository it was built with;
#   - `update --check` sees 9.9.9 and changes nothing;
#   - a tampered archive and a wrongly signed checksum file are both refused, with 9.9.8 still in place;
#   - `update` installs 9.9.9, which then reports its version and passes self-check;
#   - a second `update` finds nothing newer;
#   - install.sh (Unix) installs 9.9.9 from the same release, and refuses the tampered and wrongly signed ones.
#
# Usage: tools/release/update-e2e.sh <rid>      e.g. linux-x64, osx-arm64

set -euo pipefail

rid="${1:?usage: update-e2e.sh <rid>}"
repo="$(cd "$(dirname "$0")/../.." && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

archive_ext=tar.gz
exe=mysync
case "$rid" in win-*) archive_ext=zip; exe=mysync.exe ;; esac

echo "== test key"
openssl ecparam -name prime256v1 -genkey -noout -out "$work/test.key.pem"
openssl ec -in "$work/test.key.pem" -pubout -out "$work/test.pub.pem" 2>/dev/null
public_key="$(openssl ec -in "$work/test.key.pem" -pubout -outform DER 2>/dev/null | base64 | tr -d '\n')"
openssl ecparam -name prime256v1 -genkey -noout -out "$work/other.key.pem"

publish() {
  dotnet publish "$repo/apps/mysync/MySync.Cli.csproj" \
    --configuration Release --runtime "$rid" --self-contained true --output "$work/publish-$1" \
    -p:Version="$1" -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true -p:InvariantGlobalization=true -p:DebugType=embedded \
    -p:MySyncReleasePublicKey="$public_key" -p:MySyncReleaseRepository=mysync-e2e/fork --nologo --verbosity quiet
}

echo "== publish 9.9.8 and 9.9.9 for $rid"
publish 9.9.8
publish 9.9.9

release() { # <folder> <signing key>
  local folder="$1/mysync-9.9.9"
  mkdir -p "$folder"
  local name="mysync-9.9.9-$rid.$archive_ext"
  if [ "$archive_ext" = zip ]; then
    (cd "$work/publish-9.9.9" && zip -q "$folder/$name" "$exe")
  else
    tar --create --gzip --file "$folder/$name" --directory "$work/publish-9.9.9" "./$exe"
  fi
  if command -v sha256sum >/dev/null; then
    (cd "$folder" && sha256sum "$name" > SHA256SUMS.txt)
  else
    (cd "$folder" && shasum -a 256 "$name" > SHA256SUMS.txt)
  fi
  openssl dgst -sha256 -sign "$2" -out "$folder/SHA256SUMS.txt.sig" "$folder/SHA256SUMS.txt"
}

release "$work/releases" "$work/test.key.pem"

# A tampered archive: same signed checksums, different bytes.
cp -R "$work/releases" "$work/tampered"
printf 'x' >> "$work/tampered/mysync-9.9.9/mysync-9.9.9-$rid.$archive_ext"

# Checksums signed by someone else.
release "$work/wrong-key" "$work/other.key.pem"

mkdir -p "$work/bin"
cp "$work/publish-9.9.8/$exe" "$work/bin/$exe"
installed="$work/bin/$exe"

version_is() {
  local reported
  reported="$("$installed" --version)"
  if [ "$reported" != "$1" ]; then
    echo "FAIL: installed mysync reports '$reported', expected '$1'" >&2
    exit 1
  fi
}

expect_refused() { # <source> <what>
  if MYSYNC_UPDATE_SOURCE="$1" "$installed" update; then
    echo "FAIL: an update from $2 was accepted" >&2
    exit 1
  fi
  version_is 9.9.8
  echo "ok: $2 refused, 9.9.8 untouched"
}

version_is 9.9.8

echo "== the built-in release repository"
# A proxy that refuses every connection: nothing leaves the machine, and the error names the repository.
if HTTPS_PROXY=http://127.0.0.1:9 https_proxy=http://127.0.0.1:9 "$installed" update --check --json > "$work/default.json"; then
  echo "FAIL: update --check reached a release source through a dead proxy" >&2
  exit 1
fi
grep -q "mysync-e2e/fork" "$work/default.json" || { echo "FAIL: the binary doesn't use its built-in repository:" >&2; cat "$work/default.json" >&2; exit 1; }
echo "ok: asks GitHub mysync-e2e/fork"

echo "== update --check"
MYSYNC_UPDATE_SOURCE="$work/releases" "$installed" update --check | tee "$work/check.txt"
grep -q "9.9.9 is available" "$work/check.txt"
version_is 9.9.8

echo "== refused updates"
expect_refused "$work/tampered" "a tampered archive"
expect_refused "$work/wrong-key" "checksums signed with another key"

echo "== update"
MYSYNC_UPDATE_SOURCE="$work/releases" "$installed" update
version_is 9.9.9
"$installed" self-check

echo "== again: nothing newer"
MYSYNC_UPDATE_SOURCE="$work/releases" "$installed" update | tee "$work/again.txt"
grep -q "is up to date" "$work/again.txt"

if ls -A "$work/bin" | grep -q '^\.mysync-update-'; then
  echo "FAIL: an update workspace was left behind" >&2
  exit 1
fi

if [ "$archive_ext" = tar.gz ]; then
  install() { # <source> <into>
    MYSYNC_INSTALL_SOURCE="$1" MYSYNC_INSTALL_PUBLIC_KEY_FILE="$work/test.pub.pem" MYSYNC_INSTALL_DIR="$2" sh "$repo/install.sh"
  }

  echo "== install.sh"
  install "$work/releases" "$work/installed"
  [ "$("$work/installed/mysync" --version)" = 9.9.9 ] || { echo "FAIL: install.sh didn't install 9.9.9" >&2; exit 1; }

  for bad in tampered wrong-key; do
    if install "$work/$bad" "$work/installed-$bad"; then
      echo "FAIL: install.sh accepted the $bad release" >&2
      exit 1
    fi
    [ ! -e "$work/installed-$bad/mysync" ] || { echo "FAIL: install.sh left a binary from the $bad release" >&2; exit 1; }
    echo "ok: install.sh refused the $bad release"
  done
fi

echo "update end-to-end: all passed for $rid"
