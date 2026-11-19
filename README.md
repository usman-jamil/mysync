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
mysync sync --name next-wave   # in a folder: create a new share called next-wave
mysync sync next-wave          # in an empty folder, on another machine: clone it
mysync sync                    # from then on, in either folder: two-way sync
```

- **Two-way and hash-based.** Changes are found by SHA-256, not timestamps. An executable-bit change syncs too.
- **Content-addressed storage.** Files are stored by hash, so identical files are stored once and renames cost nothing.
- **Never silently overwrites.** When both machines changed the same file, neither change is applied. mysync reports the conflict and waits for you to decide.
- **One self-contained binary** for Windows, macOS and Linux (x64 and arm64). Credentials come from environment variables only, and mysync never prompts for them.

## Install

**macOS and Linux:**

```bash
curl -fsSL https://raw.githubusercontent.com/usman-jamil/mysync/main/install.sh | sh
```

**Windows (PowerShell):**

```powershell
irm https://raw.githubusercontent.com/usman-jamil/mysync/main/install.ps1 | iex
```

Then open a new terminal and check it:

```bash
mysync --version      # e.g. 0.4.0
mysync self-check     # checks this binary works on this machine
```

### What the install script does

1. Picks the build for your system and processor: macOS, Linux (glibc or musl/Alpine) or Windows, on x64 or arm64.
2. Downloads the release's `SHA256SUMS.txt` and checks it is **signed with the mysync release key**, which is written into the script. A release that isn't signed, or is signed with another key, is refused.
3. Downloads the archive and checks it against the signed checksum.
4. Runs the new binary's `--version` and `self-check`.
5. Only then installs it, into a folder that needs no `sudo` or administrator rights:
   - macOS and Linux: `~/.local/bin/mysync`. If that folder isn't on your `PATH`, the script prints the line to add to `~/.zshrc` or `~/.bashrc`.
   - Windows: `%LOCALAPPDATA%\Programs\mysync\mysync.exe`, added to your user `PATH`.

If any check fails, nothing is installed and the script says why. The binary isn't quarantined (macOS) or flagged by SmartScreen (Windows), because it wasn't downloaded by a browser.

**Requirements:**
- **macOS and Linux:** `curl`, `tar`, `openssl`, and `sha256sum` or `shasum`. These are present by default on macOS and most Linux distributions.
- **Alpine:** run `apk add curl openssl libstdc++ libgcc` first.
- **Windows:** Windows PowerShell 5.1 or PowerShell 7. Nothing else.

### Install options

| Variable | Default | Meaning |
| --- | --- | --- |
| `MYSYNC_VERSION` | the latest release | Install this release instead, e.g. `0.4.0`. |
| `MYSYNC_INSTALL_DIR` | `~/.local/bin`, or `%LOCALAPPDATA%\Programs\mysync` on Windows | Install into this folder. |
| `GITHUB_TOKEN` | | Only needed if GitHub rate-limits you. |

Variables go to the shell that runs the script:

```bash
curl -fsSL https://raw.githubusercontent.com/usman-jamil/mysync/main/install.sh | MYSYNC_VERSION=0.4.0 sh
```

```powershell
$env:MYSYNC_VERSION = '0.4.0'; irm https://raw.githubusercontent.com/usman-jamil/mysync/main/install.ps1 | iex
```

**Read the script first?** Download it, look, then run it:

```bash
curl -fsSLO https://raw.githubusercontent.com/usman-jamil/mysync/main/install.sh && less install.sh && sh install.sh
```

0.4.0 is the first signed release, so the scripts (and `mysync update`) can't install 0.3.0 or earlier.

### Coming from 0.3.0 or earlier

Those versions have no `update` command. Run the install script once on each machine; from then on, use `mysync update`.

If you had put mysync in `/usr/local/bin` by hand, either delete that copy (`sudo rm /usr/local/bin/mysync`) or make sure `~/.local/bin` comes first on your `PATH`. Otherwise the old copy keeps running. `command -v mysync` shows which one runs.

### Install by hand

Download these from the [latest release](https://github.com/usman-jamil/mysync/releases/latest): the archive for your platform, `SHA256SUMS.txt` and `SHA256SUMS.txt.sig`.

| Platform | Archive |
| --- | --- |
| macOS (Apple silicon / Intel) | `mysync-<version>-osx-arm64.tar.gz` / `osx-x64` |
| Linux (x64 / arm64, glibc) | `mysync-<version>-linux-x64.tar.gz` / `linux-arm64` |
| Linux (x64, musl / Alpine) | `mysync-<version>-linux-musl-x64.tar.gz` |
| Windows (x64 / arm64) | `mysync-<version>-win-x64.zip` / `win-arm64` |

Check the signature and the checksum, then install:

```bash
curl -fsSLO https://raw.githubusercontent.com/usman-jamil/mysync/main/tools/release/mysync-release-signing.pub.pem
openssl dgst -sha256 -verify mysync-release-signing.pub.pem -signature SHA256SUMS.txt.sig SHA256SUMS.txt   # "Verified OK"
sha256sum -c --ignore-missing SHA256SUMS.txt          # Linux
shasum -a 256 -c --ignore-missing SHA256SUMS.txt      # macOS
tar -xzf mysync-<version>-<platform>.tar.gz && mkdir -p ~/.local/bin && mv mysync ~/.local/bin/
```

The public key is [`tools/release/mysync-release-signing.pub.pem`](tools/release/mysync-release-signing.pub.pem). The binaries themselves aren't code-signed, so a browser download triggers the OS warning:
- **macOS** quarantines it; clear that once with `xattr -d com.apple.quarantine ~/.local/bin/mysync`.
- **Windows** SmartScreen warns the first time; choose *More info* → *Run anyway*. On Windows, unzip the archive and put `mysync.exe` in a folder on your `PATH`; `(Get-FileHash mysync-<version>-win-x64.zip).Hash` should match its line in `SHA256SUMS.txt`.

### Uninstall

Delete the binary: `~/.local/bin/mysync`, or `%LOCALAPPDATA%\Programs\mysync` on Windows (and remove that folder from your user `PATH`). mysync installs nothing else. Synced folders keep their files and their `.mysynctool` state.

## Updating

```bash
mysync update --check            # is there a newer release? Downloads nothing
mysync update                    # install the latest release
mysync update --version 0.4.0    # install exactly this release, newer or older, e.g. to keep a team on one version
mysync update --prerelease       # let "latest" include prereleases
```

```text
$ mysync update --check
mysync 0.4.0 is up to date (from GitHub usman-jamil/mysync)
```

- **Checked before anything is replaced.** `update` makes the same checks as the install script: the signature on `SHA256SUMS.txt`, the archive's checksum, then the new binary's `--version` and `self-check`. Only then does it swap the binary in. If anything fails, the installed mysync stays exactly as it was. mysync only accepts its own release key; no setting or environment variable can make it trust another.
- **Takes effect from the next command.**
  - macOS and Linux: the new file replaces the old one in place.
  - Windows: a running `.exe` can't be overwritten. The old one is renamed aside (`mysync.exe.old-…`) and deleted the next time mysync starts.
- **Latest never goes backwards.** `mysync update` only moves to a newer release. Going back needs an explicit `--version`.
- **Update machines together on significant changes.** When the first or second number changes (0.3 → 0.4), `update` says so. Read the release notes and update every machine that shares folders together.
- **Never automatic.** mysync contacts GitHub only when you run `mysync update`. There are no background checks and no "new version" hints.
- **Permissions.** For a binary in a folder you can't write (such as `/usr/local/bin`), `update` stops before downloading anything (exit code `4`). Run `sudo mysync update`, or reinstall into `~/.local/bin` with the install script.
- **A development build** (`dotnet run`) can't update itself (exit code `4`).
- **Rate limits.** If GitHub rate-limits you, set `GITHUB_TOKEN`. It's sent only to `api.github.com` and never printed.
- **Machines without internet access.** `MYSYNC_UPDATE_SOURCE=/path/to/releases` updates from a local folder laid out like the GitHub releases (`mysync-0.4.0/<the release's files>`). The same signature checks apply, so a mirror can't serve a binary of its own.
- **Scripts.** Add `--json` for one result document: `status` is `up-to-date`, `available` or `updated`, plus `currentVersion`, `targetVersion` and `exitCode`. An unknown version exits `8`.

## Configure

Environment variables only: no config file, no prompts.

| Variable | Required | Default | Meaning |
| --- | --- | --- | --- |
| `AZURE_STORAGE_CONNECTION_STRING` | yes | | The storage account. `UseDevelopmentStorage=true` targets a local Azurite. |
| `MYSYNC_CONTAINER` | no | `mysync` | Blob container (3–63 lowercase letters, digits and hyphens). Created when the first share is. |
| `MYSYNC_PROVIDER` | no | `azure-blob` | Storage provider. Azure Blob is the only one today. |
| `MYSYNC_PARALLELISM` | no | `min(cores × 2, 16)` | Concurrent transfers, 1–64. `--parallel <n>` overrides it for one run. |
| `MYSYNC_PUBLISH_CONNECTION_STRING` | for publishing | | The storage account publications go to. Its static website must be on. See [Publishing](#publishing-a-share). |
| `MYSYNC_PUBLISH_BASE_URL` | for publishing | | That account's static website address, `https://<account>.z13.web.core.windows.net` (or your own domain). |

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
mysync sync --name q3-report
#   Created share q3-report (0b6f3c1e-8f0a-4c43-9d3e-5a1f2b7c9e10): 142 files, 38.2 MB
#   Share it with:  mysync sync q3-report

# Machine B, in an empty folder
mkdir ~/report && cd ~/report
mysync sync q3-report

# From then on, on either machine, inside the folder
mysync sync
```

Each `mysync sync` uploads what changed here and downloads what changed there. It runs only when you run it; there is no background process.

## Commands

| Command | What it does |
| --- | --- |
| `mysync sync --name <name>` | Create a share from this folder, with that name. |
| `mysync sync` | Two-way sync this folder's share. |
| `mysync sync <name>` | Clone a share into this (empty) folder, by its name (or its share ID). |
| `mysync shares` | List every named share: its name and share ID. |
| `mysync rename <name>` | Give this folder's share a new name (or a first one, for a share from an older version). |
| `mysync status` | Show what `sync` would do, plus conflicts and warnings. Changes nothing. |
| `mysync info [<name>]` | The share's names and ID, storage, this folder's revision against the share's, file count and size, open conflicts. |
| `mysync conflicts` | List open conflicts: path, kind, both versions, and who changed the share. Works offline. |
| `mysync diff [<path>...]` | Show what differs between your version of a file and the share's, ending with the `resolve` commands to paste. No paths: every open conflict. |
| `mysync resolve <path>... --keep local\|remote` | Decide conflicts. `--all` instead of paths decides every one. |
| `mysync gc` | Delete stored files no recent revision uses, and old history. |
| `mysync delete [<name>] --yes` | Delete a share for every machine: its stored files, history and publications, and free its name. Local files stay. |
| `mysync publish` | Publish a snapshot of the share as a read-only website anyone with the link can browse and download from. |
| `mysync publications` | List the share's publications and their links. |
| `mysync unpublish <id>` | Take a publication down; its link stops working at once. |
| `mysync update` | Update mysync itself to the latest release, verified before it replaces anything. |
| `mysync self-check` | Check this binary works on this machine. |

Options for `sync`:

- `-n, --dry-run`: show the plan without changing anything.
- `--no-delete`: delete nothing, here or in the share; deletions wait for a later run.
- `--allow-mass-delete`: see [Safety checks](#safety-checks).
- `-y, --yes`: don't ask before creating a large share.
- `--name <name>`: the new share's name (required when creating one; ignored, with a warning, in a folder that's already a share).
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

## Names

Every share has a name, like `q3-report`, so you can type `mysync sync q3-report` on a server instead of a long share ID. Anywhere mysync asks for a share (`sync`, `info`, `delete`, `--share`), the name and the share ID both work.

- **Required and unique.** `mysync sync` in a new folder asks for one, and suggests a name from the folder's name. No two shares can have the same name; mysync says which share already has it.
- **Lowercase letters, digits and `-`**, 3 to 40 characters, starting and ending with a letter or digit. Capitals are lowercased (`Q3-Report` is `q3-report`). A name can't look like a share ID.
- **`mysync shares`** lists every name with its share ID.
- **`mysync rename <name>`** gives a share a new name (the old one is freed), or names a share created by an older version of mysync.
- **Deleting a share frees its name**, whether you deleted it by name or by ID, so the name can be used again.
- **If creating a share was interrupted**, its name is taken but the share was never finished. `mysync shares` still lists it; `mysync delete <name> --yes` removes it and frees the name.
- **Storage lifecycle rules:** names are small blobs under `aliases/` in the container. A rule such as "delete blobs 90 days after creation" deletes them too: the name disappears from `mysync shares`, stops working, and someone else could claim it, while the share itself may live on. When you write lifecycle rules, include or exclude `aliases/` on purpose.

## Conflicts

If both machines changed the same file since they last agreed, mysync applies neither change:

- your file stays as it is;
- the share keeps its version;
- the conflict is reported at the end of the run, in `mysync conflicts`, and in `.mysynctool.log`.

The rest of the sync goes ahead, and the run exits with code 3. Only the machine that syncs second sees the conflict, because it's the one whose change was held back.

To decide, look first, then choose: `mysync conflicts` → `mysync diff` → `mysync resolve`.

```text
$ mysync diff reports/q3.csv
reports/q3.csv  modify/modify  local 51.0 KB (you, 08:55)  ·  remote 50.9 KB (ali@SERVER01, rev 18, 07:12)
--- local/reports/q3.csv
+++ remote/reports/q3.csv
@@ -10,7 +10,7 @@
 north,q3,1200
-south,q3,980
+south,q3,1010
 east,q3,1430
Keep yours:    mysync resolve reports/q3.csv --keep local
Keep theirs:   mysync resolve reports/q3.csv --keep remote
Or edit the file to combine both, then keep yours.
```

```bash
mysync conflicts                                # what's open, with a diff command per path
mysync diff                                     # every open conflict, local against the share's version
mysync diff reports/q3.csv --base               # what each side changed since you last agreed
mysync resolve reports/q3.csv --keep local      # upload my version
mysync resolve reports/q3.csv --keep remote     # take the share's version
mysync resolve --all --keep remote              # every open conflict at once
```

- **Want both changes?** Edit your file so it holds both, then `--keep local`.
- **Prefer your own merge tool?** Save the share's version outside the folder (never inside it, or it would sync): `mysync diff reports/q3.csv --print remote > /tmp/q3.remote.csv`. `--print local` and `--print base` work too.
- **`diff` changes nothing.** It works while a sync runs, needs storage access for the share's versions, and exits 0 whether or not there are differences (scripts use `--json`).
- **Options:** `--base`; `-U, --context <n>` lines of context (default 3); `--stat` one line per path; `--print local|remote|base` (one path, nothing else on stdout).
- **A path that isn't in conflict** is compared with the share's current version: what someone else changed, or what your next sync would upload.
- **What you'll see:** binary files (and anything that isn't UTF-8 or UTF-16 with a BOM) as one line with sizes and hashes; "Only line endings differ (local: CRLF, remote: LF)" instead of a diff of every line; the executable bit when only that changed; files over 8 MiB (or 200,000 lines) as "too large", with the `--print` hint. Piped into a file, the output is a patch `git apply` accepts.

`resolve` touches only the paths you name. If the share's version changed again since the conflict was recorded, it stays a conflict rather than being overwritten. A conflict also clears on its own at the next sync in two cases:
- you make both sides identical;
- you put your file back as it was, and the share's version then downloads.

## Deleting a share

When the work is done, for example to remove sensitive data from the storage account:

```bash
mysync delete --yes                    # in the share's folder
mysync delete <shareId> --yes          # or from anywhere
```

- **Everything is removed from storage:** every stored file and the share's history, and every [publication](#publishing-a-share) of it first. What stays is a small marker saying who deleted the share and when.
- **Publications need the publishing settings.** If the share has publications, set `MYSYNC_PUBLISH_CONNECTION_STRING` and `MYSYNC_PUBLISH_BASE_URL` first, or `delete` stops (exit code 2) before changing anything: otherwise the public copies would be left with no record of them.
- **Other machines are told.** Their next `sync` (or `status`, `info`, `resolve`, `gc`) stops with "Share … was deleted by … on …" and exit code 8. **Their files are left alone**: whoever has a copy decides what to do with it. Running `mysync delete --yes` in such a folder removes its `.mysynctool`, making it an ordinary folder again.
- **This folder keeps its files.** Its `.mysynctool` and `.mysynctool.log` are removed, so it's an ordinary folder again.
- **Interrupted?** Run it again: it finishes the job.
- `--yes` is required: deleting can't be undone, and mysync never asks.
- **Soft delete and blob versioning:** if the storage account has either turned on, Azure keeps deleted data until its retention period ends. `mysync delete` warns when it finds such copies. Purge them in Azure, or turn those features off, if the data must be gone immediately. Clients older than 0.3 can't read the marker and stop with "upgrade mysync"; they don't delete anything either.

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

## Publishing a share

`mysync publish` makes a **public, read-only website** from a snapshot of a share: a page where anyone with the link can browse the folders, search, and download files one at a time or all at once. No login, no server. Use it to hand files to someone who doesn't use mysync.

```text
$ mysync publish --title "Q3 reports"
Published Q3 reports: revision 18, 142 files (38.2 MB)
  https://teampublish.z13.web.core.windows.net/p/4fQ9xK2mT7bV1nR8sL3pZa/
Anyone with this link can browse and download these files. Remove it with:
  mysync unpublish 4fQ9xK2mT7bV1nR8sL3pZa
Download everything:
  curl -fsSL https://teampublish.z13.web.core.windows.net/p/4fQ9xK2mT7bV1nR8sL3pZa/download.sh | sh
  irm https://teampublish.z13.web.core.windows.net/p/4fQ9xK2mT7bV1nR8sL3pZa/download.ps1 | iex
```

### Setting up publishing (once per team)

Publishing needs a storage account with Azure's **static website** hosting turned on. Use a **separate account** just for publishing: its public address then names nothing about the account that holds your shares. The steps below use the [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli) (`az login` first); the [Azure portal](https://portal.azure.com) equivalents are in brackets.

**1. Create the publishing account** (skip this if you already have one). It must be a general-purpose v2 account. [Portal: *Storage accounts* → *Create*, Performance *Standard*, Redundancy *LRS* is enough.]

```bash
az storage account create --name teampublish --resource-group my-rg --location westeurope \
  --kind StorageV2 --sku Standard_LRS --min-tls-version TLS1_2
```

Account names are 3–24 lowercase letters and digits, unique across Azure.

**2. Turn on static website hosting**, with exactly these document names; mysync checks them. [Portal: the account → *Data management* → *Static website* → *Enabled*, index document `index.html`, error document `404.html`.]

```bash
az storage blob service-properties update --account-name teampublish \
  --static-website --index-document index.html --404-document 404.html
```

This creates the `$web` container. Only what's in `$web` is served publicly. You don't need to allow anonymous blob access on the account, and mysync never changes these settings itself.

**3. Get the website's address.** This is `MYSYNC_PUBLISH_BASE_URL`. [Portal: *Static website* → *Primary endpoint*.]

```bash
az storage account show --name teampublish --resource-group my-rg --query primaryEndpoints.web --output tsv
# https://teampublish.z13.web.core.windows.net/
```

**4. Get the connection string.** This is `MYSYNC_PUBLISH_CONNECTION_STRING`. Use the one with the account key: mysync needs it to check the static website settings and to write the publication. [Portal: *Security + networking* → *Access keys* → *Connection string*.]

```bash
az storage account show-connection-string --name teampublish --resource-group my-rg --output tsv
```

The connection string is a secret, like `AZURE_STORAGE_CONNECTION_STRING`: anyone who has it can publish, change or remove every publication. Share it like the other one, never in a file you commit.

**5. Set both variables** on each machine that publishes, next to `AZURE_STORAGE_CONNECTION_STRING`:

```bash
# macOS / Linux (e.g. in ~/.zshrc or ~/.bashrc)
export MYSYNC_PUBLISH_CONNECTION_STRING="DefaultEndpointsProtocol=https;AccountName=teampublish;AccountKey=..."
export MYSYNC_PUBLISH_BASE_URL="https://teampublish.z13.web.core.windows.net"
```

```powershell
# Windows PowerShell, this session only
$env:MYSYNC_PUBLISH_CONNECTION_STRING = "DefaultEndpointsProtocol=https;AccountName=teampublish;AccountKey=..."
$env:MYSYNC_PUBLISH_BASE_URL = "https://teampublish.z13.web.core.windows.net"
```

**6. Try it** in a share's folder:

```bash
mysync publish --title "Test"      # prints the link; open it in a browser
mysync publications                # lists it
mysync unpublish <id>              # removes it
```

Files are copied **inside Azure**, from your share's account straight to the publishing account, when `AZURE_STORAGE_CONNECTION_STRING` contains that account's key (the usual case): mysync signs a short-lived, read-only link to each file. If it's a SAS connection string instead, files are copied through your machine, and `publish` says so.

Only the machines that publish need these two variables, plus any machine that runs `mysync delete` on a share that has publications.

**Using the share account instead:** the two publishing variables may name the same account as `AZURE_STORAGE_CONNECTION_STRING`, once static website hosting is on there (steps 2–3). Your shares stay private, since only `$web` is public, but every public link then names that account, and `publish` says so.

**If something's wrong**, `publish` stops before uploading anything and says what to fix:

| Message | What to do |
| --- | --- |
| `MYSYNC_PUBLISH_… is not set` (exit 2) | Set both variables (step 5). |
| `MYSYNC_PUBLISH_BASE_URL is not valid` (exit 2) | Use the full `https://` address from step 3, with no `?` or `#` part. |
| `Static website hosting is off` / `serves '…' as its index document` (exit 2) | Run step 2; the message shows the exact command for your account. |
| `can't read the service properties` (exit 8) | The connection string has no account key (e.g. it's a SAS). Use the one from step 4. |
| `The publishing account rejected the credentials` (exit 8) | The key in `MYSYNC_PUBLISH_CONNECTION_STRING` is wrong or was rotated; copy it again (step 4). |
| Published, but exit code 10 | The files are up, but the page couldn't be opened from the internet. `MYSYNC_PUBLISH_BASE_URL` is probably not this account's static website address; check step 3. |
| `built without its publication page` | This mysync is a development build. Install a release (see [Install](#install)). |

### Using it

| Command | |
| --- | --- |
| `mysync publish` | Publish this folder's share as it was last synced. `--title` names the page (default: the folder's name). `--revision <n>` publishes an older revision. `--share <id>` publishes another share. |
| `mysync publications` | The share's publications: ID, status, link, revision, when, files and size. |
| `mysync unpublish <id>` | Remove a publication. Its link stops working first, then its files are removed. |

- **A snapshot, and a copy.** The files are copied inside Azure (see above). Later syncs, `gc` or conflict resolution don't change a publication; publish again for a newer one. If this folder has changes that aren't synced, `publish` says so: it publishes the last synced state.
- **The link is the secret.** Its 22-character ID is random and never derived from the share ID. Nothing public leads back to your share. Search engines are asked to stay away (`robots.txt`, `noindex`), but anyone who has the link can open it, so treat it like the files themselves.
- **Safe to open.** Web pages, SVG, XML and scripts in a publication always download instead of opening, so a published file can't run code in the browser. Text, Markdown, CSV, JSON, PDF and images open in a new tab.
- **Download everything:** the page and the output show one command for macOS/Linux and one for Windows. Run it in an empty folder: it downloads every file and checks each SHA-256 against `SHA256SUMS.txt`, stopping at the first mismatch.
- **Exit code 10:** the publication is up, but mysync couldn't open it anonymously. Usually `MYSYNC_PUBLISH_BASE_URL` isn't the account's static website address.
- **Deleting the share removes its publications too** (see [Deleting a share](#deleting-a-share)).

## Scripting: `--json` and exit codes

With `--json`, every command prints exactly one JSON document on stdout, even when it fails, and that document ends with `exitCode`. Progress, warnings and errors always go to stderr. When output isn't a terminal it is plain text: no colours, escape codes or line wrapping.

| Exit code | Meaning |
| --- | --- |
| 0 | Success |
| 1 | Unexpected error (details with `--verbose`) |
| 2 | Usage or configuration error (bad option, missing or invalid environment variable, `resolve` on a path not in conflict, the publishing account's static website is off) |
| 3 | Finished, but conflicts are open (takes precedence over 9) |
| 4 | Folder precondition failed: not empty, already a share, not a share, or an unsafe root; the name is taken; `update` can't replace this binary |
| 5 | Another machine kept committing; try again |
| 6 | Another mysync is working in this folder |
| 7 | The mass-delete safety check stopped the sync |
| 8 | Share not found, deleted, never finished, or no share has that name; the storage account rejected the credentials; or no such release or publication |
| 9 | Finished, but some paths were skipped (busy, unreadable, invalid name) |
| 10 | Published, but the publication couldn't be opened from the internet (check `MYSYNC_PUBLISH_BASE_URL`) |
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

Contributions are welcome: [`CONTRIBUTING.md`](CONTRIBUTING.md) covers setup, tests, where a change goes, and pull requests.

Requires the .NET 10 SDK, Node (for Nx; see `.nvmrc`) and Docker for the Azurite suites. `CLAUDE.md` describes the architecture and the rules for changing it.

```bash
npm install
npx nx build mysync
npx nx run-many -t test --projects=tag:type:unit-test,tag:type:architecture-test
npx nx run-many -t test --projects=tag:type:integration-test,tag:type:e2e-test   # Docker, or set MYSYNC_TEST_AZURITE_CONNECTION_STRING
dotnet run --project apps/mysync -- self-check --verbose
npx nx publish mysync --rid=osx-arm64          # single-file binary in out/osx-arm64
```

- **The publication page** (`apps/mysync-site`, React): `npm run site` shows it with a mocked share; `apps/mysync-site/README.md` describes it.
- **Local Azurite:** `docker run -p 10000:10000 mcr.microsoft.com/azure-storage/azurite azurite-blob --blobHost 0.0.0.0 --skipApiVersionCheck`, then `export AZURE_STORAGE_CONNECTION_STRING=UseDevelopmentStorage=true`.
- **Real Azure:** the manually triggered *Real Azure tests* workflow runs the storage and end-to-end suites against a test-only account. With *large* ticked, it also runs a 300 MB file and a 5,000-file share. Add the repository secret `MYSYNC_REAL_AZURE_CONNECTION_STRING` first; the top of `.github/workflows/real-azure.yml` explains how.
- **Releases** are cut by pushing a `mysync-<version>` tag after bumping `<Version>` in `Directory.Build.props` (`CLAUDE.md` §16).
- **Forks** can publish their own releases: generate a key (`openssl ecparam -name prime256v1 -genkey -noout -out key.pem`), add its contents as the repository secret `MYSYNC_RELEASE_SIGNING_KEY`, and push a tag. Binaries built that way update from the fork and trust only its key. If you host the install scripts yourself, change the repository and public key at the top of each.
