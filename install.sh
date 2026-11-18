#!/bin/sh
# Installs mysync on macOS or Linux (README "Install"). After this, `mysync update` keeps it current.
#
#   curl -fsSL https://raw.githubusercontent.com/usman-jamil/mysync/main/install.sh | sh
#
# Downloads the release archive for this machine, checks that SHA256SUMS.txt is signed with the
# mysync release key and that the archive matches it, and installs the binary into
# ${MYSYNC_INSTALL_DIR:-$HOME/.local/bin}. Nothing is installed if any check fails.
#
# Settings (all optional):
#   MYSYNC_VERSION      a release such as 0.4.0 (default: the latest)
#   MYSYNC_INSTALL_DIR  where to put the binary (default: ~/.local/bin, no sudo needed)
#   GITHUB_TOKEN        for a private repository or GitHub's rate limit
# For testing only:
#   MYSYNC_INSTALL_SOURCE           a local folder laid out like GitHub releases, instead of GitHub
#   MYSYNC_INSTALL_PUBLIC_KEY_FILE  trust this public key instead of the release key

set -eu

# A fork that hosts its own copy of this script changes these two: its repository, and the public
# half of its MYSYNC_RELEASE_SIGNING_KEY (CLAUDE.md §16).
repository="usman-jamil/mysync"
install_dir="${MYSYNC_INSTALL_DIR:-$HOME/.local/bin}"

release_key='-----BEGIN PUBLIC KEY-----
MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEXY20aDASj1RWM+Vq4jdThyocT2Ge
mY1VkAKYECLHhDwSH413cffvkt9T14jYf8QX8FJIM/+WCXjHdtV/wudqEg==
-----END PUBLIC KEY-----'

fail() { echo "mysync install: $*" >&2; exit 1; }

case "$(uname -s)" in
  Darwin) os=osx ;;
  Linux) os=linux ;;
  *) fail "unsupported system $(uname -s); on Windows use install.ps1" ;;
esac

case "$(uname -m)" in
  x86_64 | amd64) arch=x64 ;;
  arm64 | aarch64) arch=arm64 ;;
  *) fail "unsupported processor $(uname -m)" ;;
esac

rid="$os-$arch"
if [ "$os" = linux ] && { [ -f /etc/alpine-release ] || (ldd --version 2>&1 | grep -qi musl); }; then
  [ "$arch" = x64 ] || fail "there is no musl build for $arch"
  rid="linux-musl-x64"
fi

for tool in tar; do command -v "$tool" >/dev/null || fail "$tool is required"; done
command -v openssl >/dev/null || fail "openssl is required to check the release signature"
if command -v sha256sum >/dev/null; then sha256() { sha256sum "$1" | cut -d' ' -f1; }
elif command -v shasum >/dev/null; then sha256() { shasum -a 256 "$1" | cut -d' ' -f1; }
else fail "sha256sum or shasum is required"; fi

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

fetch() { # <url> <file>
  if [ -n "${GITHUB_TOKEN:-}" ]; then
    curl -fsSL -H "Authorization: Bearer $GITHUB_TOKEN" -o "$2" "$1"
  else
    curl -fsSL -o "$2" "$1"
  fi
}

if [ -n "${MYSYNC_INSTALL_SOURCE:-}" ]; then
  if [ -z "${MYSYNC_VERSION:-}" ]; then
    MYSYNC_VERSION="$(ls "$MYSYNC_INSTALL_SOURCE" | sed -n 's/^mysync-\([0-9][0-9.]*\)$/\1/p' | sort -t. -k1,1n -k2,2n -k3,3n | tail -n 1)"
  fi
  [ -n "$MYSYNC_VERSION" ] || fail "no release found in $MYSYNC_INSTALL_SOURCE"
  get() { cp "$MYSYNC_INSTALL_SOURCE/mysync-$MYSYNC_VERSION/$1" "$work/$1" 2>/dev/null || fail "$1 is missing from mysync $MYSYNC_VERSION"; }
else
  command -v curl >/dev/null || fail "curl is required"
  if [ -z "${MYSYNC_VERSION:-}" ]; then
    fetch "https://api.github.com/repos/$repository/releases/latest" "$work/latest.json" || fail "could not reach GitHub"
    MYSYNC_VERSION="$(sed -n 's/.*"tag_name": *"mysync-v\{0,1\}\([^"]*\)".*/\1/p' "$work/latest.json" | head -n 1)"
    [ -n "$MYSYNC_VERSION" ] || fail "could not find the latest release"
  fi
  get() { fetch "https://github.com/$repository/releases/download/mysync-$MYSYNC_VERSION/$1" "$work/$1" || fail "$1 is missing from mysync $MYSYNC_VERSION"; }
fi

archive="mysync-$MYSYNC_VERSION-$rid.tar.gz"
echo "Installing mysync $MYSYNC_VERSION ($rid) into $install_dir"

get SHA256SUMS.txt
get SHA256SUMS.txt.sig
get "$archive"

key="$work/release.pub.pem"
if [ -n "${MYSYNC_INSTALL_PUBLIC_KEY_FILE:-}" ]; then cp "$MYSYNC_INSTALL_PUBLIC_KEY_FILE" "$key"; else printf '%s\n' "$release_key" > "$key"; fi

openssl dgst -sha256 -verify "$key" -signature "$work/SHA256SUMS.txt.sig" "$work/SHA256SUMS.txt" >/dev/null 2>&1 \
  || fail "SHA256SUMS.txt is not signed with the mysync release key; nothing was installed"

expected="$(awk -v name="$archive" '{ file = $2; sub(/^\*/, "", file); if (file == name) print $1 }' "$work/SHA256SUMS.txt")"
[ -n "$expected" ] || fail "$archive is not listed in SHA256SUMS.txt"
[ "$(sha256 "$work/$archive")" = "$expected" ] || fail "$archive does not match its checksum; nothing was installed"

mkdir -p "$work/unpacked"
tar -xzf "$work/$archive" -C "$work/unpacked" ./mysync 2>/dev/null || tar -xzf "$work/$archive" -C "$work/unpacked" mysync
chmod 755 "$work/unpacked/mysync"

reported="$("$work/unpacked/mysync" --version)" || fail "the downloaded mysync doesn't run on this machine"
[ "$reported" = "$MYSYNC_VERSION" ] || fail "the downloaded mysync reports version $reported, expected $MYSYNC_VERSION"
"$work/unpacked/mysync" self-check >/dev/null || fail "the downloaded mysync failed its self-check"

mkdir -p "$install_dir"
mv -f "$work/unpacked/mysync" "$install_dir/mysync" || fail "could not write $install_dir (set MYSYNC_INSTALL_DIR to a folder you own)"

echo "Installed: $("$install_dir/mysync" --version) at $install_dir/mysync"
case ":$PATH:" in
  *":$install_dir:"*) ;;
  *) echo "Add it to your PATH, e.g. in ~/.zshrc or ~/.bashrc:  export PATH=\"$install_dir:\$PATH\"" ;;
esac
echo "Keep it current with: mysync update"
