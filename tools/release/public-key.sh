#!/bin/sh
# Prints the public half of $SIGNING_KEY (a PEM P-256 private key) as base64 SubjectPublicKeyInfo:
# what -p:MySyncReleasePublicKey takes. Fails if the key is missing or isn't P-256.
#
# The release workflow runs this in each job that needs the key, instead of passing it between
# jobs: GitHub masks every line of a secret, and a line of an EC private key's PEM can also appear
# inside its public key's base64, which empties any job output that carries it (CLAUDE.md §16).

set -eu

[ -n "${SIGNING_KEY:-}" ] || { echo "public-key.sh: SIGNING_KEY is empty" >&2; exit 1; }

pem="$(printf '%s\n' "$SIGNING_KEY" | openssl ec -pubout 2>/dev/null)" \
  || { echo "public-key.sh: SIGNING_KEY is not a PEM EC private key" >&2; exit 1; }
printf '%s\n' "$pem" | openssl ec -pubin -text -noout 2>/dev/null | grep -q prime256v1 \
  || { echo "public-key.sh: SIGNING_KEY must be a P-256 (prime256v1) key" >&2; exit 1; }

printf '%s\n' "$pem" | grep -v -- ----- | tr -d '\n'
