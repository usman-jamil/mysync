# mysync

[![Release](https://img.shields.io/github/v/release/usman-jamil/mysync?display_name=release&label=release)](https://github.com/usman-jamil/mysync/releases/latest)
[![CI](https://github.com/usman-jamil/mysync/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/usman-jamil/mysync/actions/workflows/ci.yml)
[![Release build](https://github.com/usman-jamil/mysync/actions/workflows/release-mysync.yml/badge.svg)](https://github.com/usman-jamil/mysync/actions/workflows/release-mysync.yml)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)](https://dotnet.microsoft.com/)
[![Platforms](https://img.shields.io/badge/platforms-Windows%20%7C%20macOS%20%7C%20Linux-informational)](#install)

```text
   _ __ ___   _   _  ___  _   _  _ __    ___
  | '_ ` _ \ | | | |/ __|| | | || '_ \  / __|
  | | | | | || |_| |\__ \| |_| || | | || (__
  |_| |_| |_| \__, ||___/ \__, ||_| |_| \___|
              |___/       |___/

    laptop                                                        server
   ~/report                                                      ~/report
      │               ┌──────────────────────────────┐               │
      │  mysync sync  │ Azure Blob container         │  mysync sync  │
      ├── local ↑ ───▶│ shares/<id>/manifest.json    │◀─── local ↑ ──┤
      │◀─ remote ↓ ───┤ shares/<id>/objects/ab/…     ├─── remote ↓ ─▶│
      │               └──────────────────────────────┘               │

         no server · hash-based · conflicts are never overwritten
```

Sync folders between machines through an Azure Blob Storage container. There is no server: shared
state lives in the container, and each folder's own state lives in a `.mysynctool` file inside it.

```text
mysync sync            # in a folder: create a new share (prints its ID), or two-way sync an existing one
mysync sync <shareId>  # in an empty folder: clone a share
```

- **Two-way and hash-based.** Changes are found by SHA-256, not timestamps. An executable-bit change syncs too.
- **Content-addressed storage.** Files are stored by hash, so identical files are stored once and renames cost nothing.
- **Never silently overwrites.** When both machines changed the same file, neither change is applied. mysync reports the conflict and waits for you to decide.
- **One self-contained binary** for Windows, macOS and Linux (x64 and arm64). Credentials come from environment variables only, and mysync never prompts for them.

## Install

Download the archive for your platform from the [latest release](https://github.com/usman-jamil/mysync/releases/latest).

| Platform | Archive |
| --- | --- |
| macOS (Apple silicon / Intel) | `mysync-<version>-osx-arm64.tar.gz` / `osx-x64` |
| Linux (x64 / arm64, glibc) | `mysync-<version>-linux-x64.tar.gz` / `linux-arm64` |
| Linux (x64, musl / Alpine) | `mysync-<version>-linux-musl-x64.tar.gz` |
| Windows (x64 / arm64) | `mysync-<version>-win-x64.zip` / `win-arm64` |

**macOS and Linux**

```bash
tar -xzf mysync-<version>-<platform>.tar.gz
sudo mv mysync /usr/local/bin/mysync
mysync --version
```

- **macOS:** the binary is unsigned, so macOS quarantines it. Clear that once: `xattr -d com.apple.quarantine /usr/local/bin/mysync`.
- **Alpine:** install the C++ runtime first: `apk add libstdc++ libgcc`.

**Windows:** unzip and put `mysync.exe` in a folder on your `PATH`. SmartScreen warns about unsigned executables the first time; choose *More info* → *Run anyway*.

**Verify the download** against the `SHA256SUMS.txt` attached to the release:

```bash
sha256sum -c --ignore-missing SHA256SUMS.txt        # Linux
shasum -a 256 -c --ignore-missing SHA256SUMS.txt    # macOS
```

```powershell
(Get-FileHash mysync-<version>-win-x64.zip).Hash    # Windows: compare with SHA256SUMS.txt
```

`mysync self-check` confirms the binary works on this machine (it checks Unicode normalization, which file-name matching depends on).

## Configure

Environment variables only: no config file, no prompts.

| Variable | Required | Default | Meaning |
| --- | --- | --- | --- |
| `AZURE_STORAGE_CONNECTION_STRING` | yes | | The storage account. `UseDevelopmentStorage=true` targets a local Azurite. |
| `MYSYNC_CONTAINER` | no | `mysync` | Blob container (3–63 lowercase letters, digits and hyphens). Created when the first share is. |
| `MYSYNC_PROVIDER` | no | `azure-blob` | Storage provider. Azure Blob is the only one today. |
| `MYSYNC_PARALLELISM` | no | `min(cores × 2, 16)` | Concurrent transfers, 1–64. `--parallel <n>` overrides it for one run. |

```bash
# macOS / Linux (e.g. in ~/.zshrc or ~/.bashrc)
export AZURE_STORAGE_CONNECTION_STRING="DefaultEndpointsProtocol=https;AccountName=...;AccountKey=...;EndpointSuffix=core.windows.net"
```

```powershell
# Windows PowerShell, this session only
$env:AZURE_STORAGE_CONNECTION_STRING = "DefaultEndpointsProtocol=https;AccountName=...;AccountKey=..."
```

The connection string is a secret. Anyone who has it can read and change every share in the account. mysync never prints, logs or stores it, including in `--verbose` output, `--json` output and `.mysynctool`. Keep it out of scripts you commit.

## Quick start

```bash
# Machine A, in the folder you want to share
cd ~/projects/report
mysync sync
#   Created share 0b6f3c1e-8f0a-4c43-9d3e-5a1f2b7c9e10 (142 files, 38.2 MB)
#   Share it with:  mysync sync 0b6f3c1e-8f0a-4c43-9d3e-5a1f2b7c9e10

# Machine B, in an empty folder
mkdir ~/report && cd ~/report
mysync sync 0b6f3c1e-8f0a-4c43-9d3e-5a1f2b7c9e10

# From then on, on either machine, inside the folder
mysync sync
```

Each `mysync sync` uploads what changed here and downloads what changed there. It runs only when you run it; there is no background process.

## Commands

| Command | What it does |
| --- | --- |
| `mysync sync` | Create a share from this folder, or two-way sync it if it already is one. |
| `mysync sync <shareId>` | Clone a share into this (empty) folder. |
| `mysync status` | Show what `sync` would do, plus conflicts and warnings. Changes nothing. |
| `mysync info` | The share ID, storage, this folder's revision against the share's, file count and size, open conflicts. |
| `mysync conflicts` | List open conflicts: path, kind, both versions, and who changed the share. Works offline. |
| `mysync resolve <path>... --keep local\|remote` | Decide conflicts. `--all` instead of paths decides every one. |
| `mysync gc` | Delete stored files no recent revision uses, and old history. |
| `mysync self-check` | Check this binary works on this machine. |

Options for `sync`:

- `-n, --dry-run`: show the plan without changing anything.
- `--no-delete`: delete nothing, here or in the share; deletions wait for a later run.
- `--allow-mass-delete`: see [Safety checks](#safety-checks).
- `-y, --yes`: don't ask before creating a large share.
- `--force`: create a share in your home folder or a drive root, or clone into a folder that isn't empty.

Options for `resolve`: `--all`, `-n, --dry-run`.

Options for `gc`:

- `-n, --dry-run`: list what would be deleted, without deleting it.
- `--keep-revisions 20`: keep everything the newest 20 revisions use.
- `--grace-hours 24`: never delete anything uploaded in the last 24 hours.

Every command also takes:

- `--json`: one JSON document on stdout.
- `--verbose` / `--quiet`: more or less logging, on stderr.
- `--parallel <n>`: concurrent transfers for this run.

## Conflicts

If both machines changed the same file since they last agreed, mysync applies neither change:

- your file stays as it is;
- the share keeps its version;
- the conflict is reported at the end of the run, in `mysync conflicts`, and in `.mysynctool.log`.

The rest of the sync goes ahead, and the run exits with code 3. Only the machine that syncs second sees the conflict, because it's the one whose change was held back.

To decide:

```bash
mysync conflicts                                # what's open
mysync resolve reports/q3.xlsx --keep local     # upload my version
mysync resolve reports/q3.xlsx --keep remote    # take the share's version
mysync resolve --all --keep remote              # every open conflict at once
```

`resolve` touches only the paths you name. If the share's version changed again since the conflict was recorded, it stays a conflict rather than being overwritten. A conflict also clears on its own at the next sync in two cases:
- you make both sides identical;
- you put your file back as it was, and the share's version then downloads.

## Ignoring files

A `.mysyncignore` file in the share root uses [gitignore syntax](https://git-scm.com/docs/gitignore):

```gitignore
*.log
build/
/secrets.env
!keep.log
```

- **It is synced**, so the whole team uses the same rules.
- **Matching is checked against git itself** in the test suite. Patterns are case-sensitive on every OS.
- **Ignored paths are neither uploaded nor downloaded**, and are never deleted here.
- **Always ignored:** mysync's own `.mysynctool*` files, `.DS_Store`, `Thumbs.db`, `desktop.ini` and `._*`. These can't be re-included.

## Safety checks

- **Mass deletes stop the sync.** If a sync would delete more than half of the files and more than 10 of them, here or in the share, it stops with exit code 7. This guards against an unmounted drive or a wrong folder. `--allow-mass-delete` lets it go ahead.
- **Large shares ask first.** Creating a share from more than 1,000 files or 1 GB asks for confirmation. Without a terminal (in scripts and CI), pass `--yes` instead.
- **Risky folders are refused.** mysync won't create a share in your home folder, a drive or volume root, or `/` without `--force`.
- **Busy files are left alone.** A file that is locked, unreadable, a symlink, or changing while it's read is skipped, reported, and retried on the next run. It is never treated as deleted.
- **Interrupting is safe.** Ctrl+C (exit 130), a crash or a lost connection never leaves a half-applied change in the share, and the next `sync` carries on from where things stand.
- **Concurrent syncs merge.** Two machines syncing at once both succeed. If another machine committed first, mysync re-plans and retries.

## Scripting: `--json` and exit codes

With `--json`, every command prints exactly one JSON document on stdout, even when it fails, and that document ends with `exitCode`. Progress, warnings and errors always go to stderr. When output isn't a terminal it is plain text: no colours, escape codes or line wrapping.

| Exit code | Meaning |
| --- | --- |
| 0 | Success |
| 1 | Unexpected error (details with `--verbose`) |
| 2 | Usage or configuration error (bad option, missing or invalid environment variable, `resolve` on a path not in conflict) |
| 3 | Finished, but conflicts are open (takes precedence over 9) |
| 4 | Folder precondition failed: not empty, already a share, not a share, or an unsafe root |
| 5 | Another machine kept committing; try again |
| 6 | Another mysync is working in this folder |
| 7 | The mass-delete safety check stopped the sync |
| 8 | Share not found, or the storage account rejected the credentials |
| 9 | Finished, but some paths were skipped (busy, unreadable, invalid name) |
| 130 | Cancelled (Ctrl+C) |

## What mysync keeps in the folder

| Path | |
| --- | --- |
| `.mysynctool` | The folder's state: which share, the last agreed version of every file, open conflicts. Don't edit or delete it. |
| `.mysynctool.lock` | Held while a command runs, so two mysync runs in one folder can't collide. |
| `.mysynctool.tmp/` | Downloads in progress, moved into place only once complete. |
| `.mysynctool.log` | Every conflict detected and resolved. Rotated at 5 MB. |

None of these is synced, and none contains credentials.

## Platform notes

- **Names that differ only in case** (`Foo.txt` and `foo.txt`) can't both exist on Windows or a default macOS volume. There, neither is downloaded, and both are reported. On Linux they're fine.
- **Names Windows can't hold** (`a:b`, `CON`, a trailing dot) are skipped on Windows with a warning.
- **Long paths** over 260 characters need long-path support enabled on Windows.
- **Symlinks and junctions** are not synced (v1). They are skipped with a warning.
- **The executable bit** syncs between Linux and macOS. Windows can't see it, so a Windows machine keeps what the share says and never strips it from a script.
- **File contents are synced byte for byte.** No line-ending conversion, ever.
- **File names are Unicode-normalized (NFC)**, so a name typed on a Mac matches the same name typed on Windows or Linux.

## Limits (v1)

- **Whole shares only.** There's no partial or selective sync; use `.mysyncignore`.
- **No delta transfer.** A changed file is uploaded again in full.
- **No per-user access control.** Anyone with the connection string reaches every share in the account.
- **`gc` safety:** run it when no one else is syncing. A sync that starts at the same moment can reuse a stored file that `gc` is about to delete.
- **Interrupted creates:** if `mysync sync` is interrupted while *creating* a share, the files it had uploaded stay in the container without a share. `gc` can't see them, because it works on one share at a time.

## Develop

Requires the .NET 10 SDK, Node (for Nx; see `.nvmrc`) and Docker for the Azurite suites. `CLAUDE.md` describes the architecture and the rules for changing it.

```bash
npm install
npx nx build mysync
npx nx run-many -t test --projects=tag:type:unit-test,tag:type:architecture-test
npx nx run-many -t test --projects=tag:type:integration-test,tag:type:e2e-test   # Docker, or set MYSYNC_TEST_AZURITE_CONNECTION_STRING
dotnet run --project apps/mysync -- self-check --verbose
npx nx publish mysync --rid=osx-arm64          # single-file binary in out/osx-arm64
```

- **Local Azurite:** `docker run -p 10000:10000 mcr.microsoft.com/azure-storage/azurite azurite-blob --blobHost 0.0.0.0 --skipApiVersionCheck`, then `export AZURE_STORAGE_CONNECTION_STRING=UseDevelopmentStorage=true`.
- **Real Azure:** the manually triggered *Real Azure tests* workflow runs the storage and end-to-end suites against a test-only account. With *large* ticked, it also runs a 300 MB file and a 5,000-file share. Add the repository secret `MYSYNC_REAL_AZURE_CONNECTION_STRING` first; the top of `.github/workflows/real-azure.yml` explains how.
- **Releases** are cut by pushing a `mysync-<version>` tag after bumping `<Version>` in `Directory.Build.props` (`CLAUDE.md` §16).
