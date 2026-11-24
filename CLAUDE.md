# CLAUDE.md — mysync

Guidance for working in this repository. This is an **Nx monorepo** that houses the **mysync** stack:

- **`apps/mysync`** — the CLI: a **.NET 10** console app (`MySync.Cli.csproj`, command name `mysync`) built with ConsoleAppFramework on `Microsoft.Extensions.Hosting`, rendering through Spectre.Console.
- **`libs/mysync/*`** — its **Clean Architecture** backend: domain, application (use cases + abstractions), infrastructure (local disk, state file, hashing), and one project per **storage provider** (Azure Blob and AWS S3).
- **`libs/shared/shared-kernel`** — dependency-free building blocks (`Result`, `Error`, `IDateTimeProvider`).
- **`tests/mysync/*`** — unit, integration and end-to-end tests (against **real** test storage, no emulator: §14), and architecture tests.

> Working name: **mysync** (executable `mysync`, local state file `.mysynctool`).
> A rename touches `AssemblyName` in `apps/mysync/MySync.Cli.csproj`, the file-name constants in `MySync.Domain.State.StateFileNames`, the release workflow's tag prefix and archive names (§16), and this file. Change them in the same commit.

The most important thing this file does is tell you **where a change belongs**. Decide which project owns it first (§2), then follow that project's rules.

---

## 1. What this is

`mysync` is an internal, cross-platform command-line tool for a small team. It moves folders between machines (laptops ↔ remote servers) through an **Azure Blob Storage container or an AWS S3 bucket** (`MYSYNC_PROVIDER`). **No server or service of our own** sits behind it: all shared state lives in the blob container, and all per-folder state lives in a `.mysynctool` file inside the synced folder.

How it's used:

```text
# Machine A — in any folder
$ mysync sync --name next-wave
Created share next-wave (0b6f3c1e-8f0a-4c43-9d3e-5a1f2b7c9e10): 142 files, 38.2 MB
Share it with:  mysync clone next-wave

# Machine B — anywhere: a new folder is created for the share (like git clone)
$ mysync clone next-wave
Cloned share next-wave (0b6f3c1e…) into ./next-wave (revision 1)
Next:  cd next-wave
# (or, in an EMPTY folder: mysync sync next-wave)

# Afterwards, on either machine, inside that folder
$ mysync sync        # two-way sync: upload local changes, download remote changes

# On a server: add files to a share without syncing (one-way, add-only, no local state, §10b)
$ mysync push next-wave /var/log/app
Pushed 214 files (1.3 GB; 187 uploaded, 27 already stored) to next-wave
  folder:  web01/20261121-051207Z/

# Optionally, a public read-only copy anyone with the link can browse and download (§10a)
$ mysync publish
Published Q3 reports: revision 18, 142 files (38.2 MB)
  https://<account>.z13.web.core.windows.net/p/4fQ9xK2mT7bV1nR8sL3pZa/
```

### Original requirements (from the product owner, keep these true)

1. Simple CLI. **Never prompts for credentials.** They come from environment variables only.
2. **No backend dependency.** Only the storage provider (Azure Blob for now).
3. `sync --name <name>` in a folder with no `.mysynctool` creates a **new share**, uploads the folder, writes `.mysynctool` and prints its name and share ID (UUID). The name is **required and unique** (§6a).
4. `sync <name|id>` clones a share into the current folder. **Allowed only in an empty folder.**
5. Later `sync` runs in a shared folder do a **two-way sync** (upload and download).
6. Change detection is **hash-based**, not timestamp-based.
7. **Content-addressed storage layout**: blob names are content hashes, not mirrored folder paths.
8. **All platforms**: Windows, Linux, macOS (x64 and arm64).
9. Built in **.NET**, published as a **single self-contained executable** with the runtime bundled.
10. Azure Blob and AWS S3 (general purpose buckets, AWS only: no S3-compatible services), behind one storage abstraction so another provider is one more project.
11. When both sides changed the same path, the change is **dropped and logged**, never auto-resolved (§9).

### Non-goals for v1

- No server, daemon, database or queue of our own.
- No real-time/watch mode (sync runs only when invoked).
- No per-user auth or ACLs. Everyone with the connection string can reach every share (accepted: internal tool, small team).
- No client-side encryption (we rely on Azure encryption at rest plus HTTPS).
- No partial/selective sync of a share. Use `.mysyncignore` to exclude things locally.
- No chunk-level dedupe or delta transfer. A changed file is re-uploaded whole.
- No config file. Environment variables are the only configuration source (§5).

---

## 2. Monorepo layout — where things go

```text
/ (repo root)
├── nx.json                       # Nx workspace config (target defaults, named inputs, caching)
├── package.json                  # Nx toolchain + mysync-site's npm packages (one lockfile) + workspace scripts
├── .nvmrc                        # Node version for CI and local work (mysync-site)
├── tsconfig.base.json, eslint.config.mjs   # shared TypeScript / ESLint settings (mysync-site)
├── mysync.slnx                   # .NET solution: every project under apps/, libs/, tests/
├── global.json                   # pins the .NET 10 SDK + selects Microsoft.Testing.Platform for dotnet test
├── Directory.Build.props         # shared MSBuild settings (nullable, analyzers, warnings-as-errors)
├── Directory.Packages.props      # Central Package Management — every NuGet version lives here
├── .editorconfig                 # formatting + analyzer severities
├── .gitattributes                # *.cs checked out as CRLF everywhere, so IDE0055 agrees on every CI OS
│
├── install.sh / install.ps1      # first install from GitHub releases, signature-checked (§16)
├── docs/                         # azure.md, aws.md (storage setup, linked from the README), ci-storage.md (the test storage and CI secrets)
├── tools/
│   ├── unicode/GenerateNfcData.cs  # file-based app: UCD → NFC tables + conformance data (§8)
│   ├── ci/check-storage-secrets.sh # every real-storage job's first step: names each missing repository secret
│   └── release/                    # release signing public key, update/install end-to-end test, test-only fixtures (§16)
│
├── apps/
│   ├── mysync/                   # MySync.Cli.csproj — the executable + composition root   (Nx: "mysync")
│   └── mysync-site/              # React 19 + Vite: the page of a published share (Nx: "mysync-site", tags stack:mysync, type:web)
│       ├── contract/site-data.schema.json   # the data contract (window.__MYSYNC_SITE__), source of truth for TS and C#
│       ├── src/                  #   app, contract types, path/URL helpers; public/site-data.js is the dev fixture
│       └── tools/                #   build plugins (CSP, notices, output contract), generate-fixture.mjs
│       ├── Program.cs            #   host, DI wiring, provider selection, command registration
│       ├── Commands/             #   <Feature>Commands.cs — thin: resolve handler, invoke, render
│       ├── Rendering/            #   everything the user sees (Spectre.Console)
│       ├── Hosting/              #   HostServiceProviderFactory (ConsoleAppFramework ↔ Microsoft.Extensions.Hosting, §4), FolderCloneRunner (§9)
│       ├── GlobalOptions.cs      #   --verbose / --quiet / --json / --parallel
│       ├── StorageProvider.cs    #   MYSYNC_PROVIDER → which provider to register
│       └── ExitCodes.cs          #   Error code → process exit code (§10)
│
├── libs/                         # class libraries, one folder per stack
│   ├── mysync/                   # backend owned exclusively by apps/mysync
│   │   ├── domain/               # MySync.Domain.csproj               (Nx: "mysync-domain")
│   │   ├── application/          # MySync.Application.csproj          (Nx: "mysync-application")
│   │   ├── infrastructure/       # MySync.Infrastructure.csproj       (Nx: "mysync-infrastructure")
│   │   ├── storage-azure/        # MySync.Storage.Azure.csproj        (Nx: "mysync-storage-azure")
│   │   ├── storage-s3/           # MySync.Storage.S3.csproj           (Nx: "mysync-storage-s3") — AWS S3 (§11 "S3 differences")
│   │   ├── updates-github/       # MySync.Updates.GitHub.csproj       (Nx: "mysync-updates-github") — `update`'s release source
│   │   ├── site-assets/          # MySync.SiteAssets.csproj           (Nx: "mysync-site-assets") — embeds the mysync-site build
│   │   └── tui/                  # MySync.Tui.csproj                  (Nx: "mysync-tui") — `mysync tui`'s presentation layer (§10d)
│   └── shared/
│       └── shared-kernel/        # SharedKernel.csproj — no dependencies (Nx: "shared-kernel")
│
├── tests/                        # test projects, mirroring libs/
│   └── mysync/
│       ├── testing/                          # MySync.Testing.csproj — shared fakes & builders (not a test project)
│       ├── domain-unit-tests/                # MySync.Domain.UnitTests.csproj
│       ├── application-unit-tests/           # MySync.Application.UnitTests.csproj
│       ├── infrastructure-unit-tests/        # MySync.Infrastructure.UnitTests.csproj
│       ├── storage-azure-integration-tests/  # MySync.Storage.Azure.IntegrationTests.csproj (real Azure test account)
│       ├── storage-s3-unit-tests/            # MySync.Storage.S3.UnitTests.csproj (a substitute IAmazonS3, a stub anonymous web)
│       ├── storage-s3-integration-tests/     # MySync.Storage.S3.IntegrationTests.csproj (real S3 test bucket)
│       ├── end-to-end-tests/                 # MySync.EndToEndTests.csproj (real storage, both providers, full DI graph, two "machines")
│       ├── site-assets-unit-tests/           # MySync.SiteAssets.UnitTests.csproj (needs the site build)
│       ├── tui-unit-tests/                   # MySync.Tui.UnitTests.csproj (update, views as snapshots, terminal, runtime)
│       └── architecture-tests/               # MySync.ArchitectureTests.csproj (dependency rules)
│
└── .github/workflows/
    ├── ci.yml                    # site checks + build once; .NET build + unit tests on ubuntu/windows/macos; real-storage suites on ubuntu
    ├── real-storage.yml          # "Large share tests": manual + weekly, both providers
    └── release-mysync.yml        # tag-driven publish of every RID (§16)
```

> **Convention:** every .NET **class library** lives under `libs/mysync/` or `libs/shared/` (one folder per project). Only the executable host (`apps/mysync/MySync.Cli.csproj`) and the web app (`apps/mysync-site`) live under `apps/`. `mysync.slnx` references all of them.
>
> `libs/mysync/` is **private to the mysync stack**. If another tool is added to this monorepo later, it gets its own `libs/<tool>/` and never references `libs/mysync/*`. `libs/shared/` is the only place a library several stacks may reference belongs.

Each project is an **Nx project** defined by its `project.json` (targets wrap `dotnet` via `nx:run-commands` unless the workspace adopts a .NET Nx plugin). Every project carries **tags**: `stack:mysync` or `stack:shared`, plus `type:lib`, `type:app`, `type:web`, `type:unit-test`, `type:integration-test`, `type:e2e-test` or `type:architecture-test`. CI and the commands in §15 select test suites by tag.

### Decision table — which project owns a change

| You are changing… | It belongs in… | Notes |
|---|---|---|
| Merge rules, manifest shape, path rules, conflict kinds, safety-guard thresholds | `libs/mysync/domain` | Pure logic. No I/O, no framework references. |
| How two versions of a file are compared (line diff, hunks, binary/encoding/line-ending detection, limits) | `libs/mysync/domain/Diffing` | Pure; `DiffLimits` is the one place for the limits. |
| Which files are stored compressed, object encodings, the share's compression setting and format 3 | `libs/mysync/domain/Content` (`CompressionPolicy`, `ObjectEncoding`), `Manifest` | Pure; the merge never sees compression. |
| Encoding uploads and decoding every read | `libs/mysync/application/Sync/Pipeline/ObjectCodec` | The **only** caller of `IShareStore.OpenObjectReadAsync` (`OneDecoderTests` checks the source). |
| Share-name rules (`ShareName`), what users type for a share (`ShareReference`), alias blob names | `libs/mysync/domain/Shares`, `ShareLayout` | `ShareReference.Parse` is the one parser every command uses. |
| How names are stored in Azure | `libs/mysync/storage-azure` (`AzureAliasStore`) | Implements `IAliasStore`; shares the container with `AzureBlobShareStore` (`AzureShareContainer`). |
| A use case (`sync`, `clone`, `resolve`, `gc`, `status`, `info`, `rename`, `shares`) or its sequencing | `libs/mysync/application/<Feature>/<UseCase>/` | Handlers own the sequence, not the I/O. |
| A new external capability (disk, clock, storage, logging sink) | interface in `libs/mysync/application/Abstractions/`, implementation in infrastructure or a storage project | Dependency inversion, see §3. |
| Local disk, `.mysynctool` serialization, hashing, ignore matching, conflict log | `libs/mysync/infrastructure` | |
| How a share is stored in Azure (blob calls, ETags, error mapping) | `libs/mysync/storage-azure` | The only library that references `Azure.Storage.Blobs`. |
| Push rules: the target folder `<host>/<timestamp>/`, where sources land, the add-only plan, the push record's shape, the filters (`--updated-since`, sizes, the decision order) | `libs/mysync/domain/Sync/Push` | Pure (`PushTarget`, `PushSourceLayout`, `PushPlanner`, `PushRecord`, `PushSince`, `ByteSize`, `PushFilter`, `PushPatterns`, `PushErrors`). |
| The `push` sequence: sources, capture of growing files, `--create`, commit retries, the share's ignore check, applying the filters | `libs/mysync/application/Sync/Push/` | `PushCommand(Handler)`, `PushFiles`, `PushSelection` (the one place filters meet the listing). Never touches sync state. |
| Pull rules: what a selection takes and where it lands, `--latest`, the pull errors | `libs/mysync/domain/Sync/Pull` | Pure (`PullSelection`, `PullRoot`, `PullFile`, `PullErrors`); push folders are read through `PushTarget.TryParseFolderName`. |
| The `pull` sequence: one HEAD, the destination checks, downloads | `libs/mysync/application/Sync/Pull/` | `PullCommand(Handler)`. Downloads through `ObjectTransfers.DownloadToStagingAsync(…, into)`; never writes to the store or sync state. |
| `mysync tui`'s folder session: the status listing, one job at a time, its events, the empty share for `--name` | `libs/mysync/application/Session/` | `GetFolderStatusQuery`, `IFolderSession` / `FolderSession`, `SessionEvents`, `InitializeEmptyShareCommand`. Front-end neutral: no terminal, keys or colours. |
| What `mysync tui` shows and how keys change it (state, update, views, the theme, the terminal, the loop) | `libs/mysync/tui` | Model–update–view (§10d); `Update` and every view are pure. References `MySync.Application` only. |
| `clone`'s folder choice (the folder given, or one named after the share) and its errors | `libs/mysync/domain/Shares/Clone` | Pure (`CloneTarget`, `CloneErrors`). |
| The `clone` sequence: resolve the share, check and create the folder, run the clone there, clean up after a failure | `libs/mysync/application/Shares/CloneInto/` | `CloneIntoFolderCommand(Handler)`, `CloneCleanUp`. Runs the unchanged `CloneShareCommand` through `ICloneRunner`. |
| `clone`'s target on disk (inspect, create with parents, remove what was created) | `libs/mysync/infrastructure/FileSystem/CloneFolders` | Implements `ICloneFolders`. |
| Running a folder-bound command in another folder (`clone`'s target) | `apps/mysync/Hosting/FolderCloneRunner` | Implements `ICloneRunner`: the composition root's own registrations, built again with `ShareRoot` set there (§9). |
| Writing into a folder other than the share root (for `pull`) | `libs/mysync/infrastructure/FileSystem/LocalFolderFactory` | Implements `ILocalFolderFactory`: a `LocalFolder` rooted anywhere, staging in `.mysync-pull.tmp/`. |
| Reading files anywhere on the machine (for `push`) | `libs/mysync/infrastructure/FileSystem/LocalSources` | Implements `ILocalSources`; the one place push enumerates files. Filters are applied to its listing by `PushSelection`, not here. |
| Publishing rules: publication IDs, the public layout, content types, the disposition policy, `site-data.js` content, download scripts | `libs/mysync/domain/Publishing` | Pure; `SiteDataBuilder` and `DownloadScripts` are string/data building only. |
| The `publish` / `unpublish` / `publications` sequence, the registry read-modify-write | `libs/mysync/application/Publishing/` | `PublishShareCommand`, `UnpublishCommand`, `ListPublicationsQuery`; `PublicationRegistryUpdater`, `PublicationRemover`. |
| How publications reach the static website (copies, headers, `$web`) | `libs/mysync/storage-azure` (`AzureStaticWebsitePublishTarget`) | Implements `IPublishTarget`. |
| How a share is stored in S3 (requests, conditional writes and their 409/412/404 answers, multipart uploads, credentials, error mapping) | `libs/mysync/storage-s3` | The only library that references `AWSSDK.*`. `S3ShareStore`, `S3AliasStore`, `S3Uploader`, `S3ErrorMapper`, `S3Connection` (credentials once, on first use). |
| How publications reach a public S3 bucket (preflight: region, anonymous probe, no public listing; copies) | `libs/mysync/storage-s3` (`S3PublishTarget`) | Implements `IPublishTarget`. |
| Which provider is used (`MYSYNC_PROVIDER`, `MYSYNC_PUBLISH_PROVIDER`) | `apps/mysync/StorageProvider.cs`, `CliEnvironment.cs`, `Program.cs` | The composition root only. |
| A new storage provider | a **new** `libs/mysync/storage-<name>` | Implements `IShareStore`, `IAliasStore`, `IPublishTarget`; passes the contract suites; registered by the composition root. |
| Where `mysync update` gets releases (GitHub API) | `libs/mysync/updates-github` | Implements `IReleaseSource`; never references Infrastructure or storage. |
| Swapping the running binary, staging an update | `libs/mysync/infrastructure/Updates` | `ExecutableInstaller` is the only OS-specific part of `update`. |
| What the CLI looks like on screen | `apps/mysync/Rendering/` | Spectre.Console. No rendering below `apps/`. |
| What a published share looks like in the browser | `apps/mysync-site` | React; renders only from `window.__MYSYNC_SITE__` (`apps/mysync-site/README.md`). Change the data shape in `contract/site-data.schema.json` first. |
| How the site build gets into the binary | `libs/mysync/site-assets` | `EmbeddedSiteAssetSource : ISiteAssetSource`; the CLI-owned `$web` root files in `Root/`. Depends on Application only. |
| A command, its arguments, flags, confirmations, exit codes | `apps/mysync/Commands/` + `ExitCodes.cs` | Keep commands thin. |
| `Result`, `Error`, other cross-stack primitives | `libs/shared/shared-kernel` | Must stay dependency-free. |
| NuGet versions | `Directory.Packages.props` | Central Package Management (§13). |
| Nx/JS tooling versions | root `package.json` | Single lockfile. |

---

## 3. Clean Architecture — golden rules

The dependency direction is the most important invariant. **Dependencies point inward only:**

```text
                 ┌──▶ MySync.Infrastructure ──┐
MySync.Cli ──────┤                            ├──▶ MySync.Application ──▶ MySync.Domain ──▶ SharedKernel
                 └──▶ MySync.Storage.Azure ───┘            │                                   ▲
                                                           └───────────────────────────────────┘
```

- `MySync.Domain` depends only on `SharedKernel`.
- `MySync.Application` depends on `MySync.Domain` + `SharedKernel`. **It must NOT reference Infrastructure, any storage project, the Azure SDK, Spectre.Console or ConsoleAppFramework.**
- `MySync.Infrastructure` and `MySync.Storage.*` depend on `MySync.Application` (and inward). They implement abstractions Application defines. **They never reference each other**, so a provider can be added or removed without touching local-disk code.
- `MySync.Storage.Azure` is the **only** project that references `Azure.*` packages, and `MySync.Storage.S3` the only one that references `AWSSDK.*` (`Amazon.*`). The two never reference each other (architecture tests check the types used and the assembly references).
- `MySync.SiteAssets`, `MySync.Updates.GitHub` and `MySync.Tui` depend on `MySync.Application` only, and nothing but the CLI (and their tests) references them. Inside `MySync.Tui`, the `State`, `Update`, `Views` and `Rendering` namespaces never reference `Terminal` or `Runtime`, the console or the disk (architecture tests). Only the CLI, its end-to-end and architecture tests, and `MySync.SiteAssets.UnitTests` need the web build.
- `MySync.Cli` is the composition root. It references Application, Infrastructure and the storage projects, and wires everything together at startup. Only it references Spectre.Console and ConsoleAppFramework.

These rules are enforced by `tests/mysync/architecture-tests` (NetArchTest). **If you add a reference that points outward, those tests fail by design. Fix the design, not the test.**

### What belongs in each project

| Project (path) | Responsibility | Contains |
|---|---|---|
| `SharedKernel` (`libs/shared/shared-kernel/SharedKernel.csproj`) | Building blocks | `Result` / `Result<T>`, `Error`, `ErrorType`, `ValidationError`, `IDateTimeProvider`. No dependencies. |
| `MySync.Domain` (`libs/mysync/domain/MySync.Domain.csproj`) | Sync rules | Value objects (`ShareId`, `ShareName`, `ShareReference`, `SharePath`, `ContentHash`, `Revision`), `ObjectEncoding`, `CompressionSetting`, `CompressionPolicy`, `AliasRecord`, `Manifest` / `ManifestEntry`, `ShareState`, `MergePlanner`, `SyncPlan` / `SyncAction`, `Conflict` / `ConflictKind`, `MassDeleteGuard`, `ShareLayout` (blob key scheme, provider-agnostic), `ShareState` + `StorageLocation` + `HashCacheEntry`, `CaseCollision`, `StateFileNames`, `NfcNormalizer` (our own Unicode NFC, §8), publishing (`PublishId`, `Publication` / `PublicationRegistry`, `PublicationLayout`, `ContentTypeMap`, `DispositionPolicy`, `SiteDataBuilder`, `DownloadScripts`), push (`PushTarget`, `PushSourceLayout`, `PushPlanner`, `PushRecord`, `PushSince`, `ByteSize`, `PushFilter`), and **static `*Errors` classes** (`ShareErrors`, `SyncErrors`, `PathErrors`, `LocalFileErrors`, `StateErrors`, `StorageErrors`, `ConfigurationErrors`, `PublishErrors`, `UpdateErrors`, `PushErrors`). No I/O. |
| `MySync.Application` (`libs/mysync/application/MySync.Application.csproj`) | Use cases | Command/query handlers, FluentValidation validators, **all abstractions** (`I...`), behaviors (decorators), response DTOs, the internal `SyncPipeline` that sequences the phases in §9. |
| `MySync.Infrastructure` (`libs/mysync/infrastructure/MySync.Infrastructure.csproj`) | Local machine | `ShareRoot` (the folder being synced), `LocalFolder` + `PathResolver` / `FileScanner`, `LocalSources` (push's reader, any path), `PushRecordSerializer`, `Sha256ContentHasher`, `JsonShareStateStore` + `FolderLock`, `FileConflictLog`, `GitIgnoreRulesLoader` + `GitIgnoreMatcher` (our own gitignore matcher), `ManifestSerializer` + `MySyncJsonContext` (source-generated JSON), `SiteDataSerializer` + `PublicationRegistrySerializer` (`Publishing/`, source-generated), `HttpPublicationVerifier` (anonymous GET), `MachineIdentity`, `DateTimeProvider`, `DependencyInjection.AddInfrastructure()`. |
| `MySync.Updates.GitHub` (`libs/mysync/updates-github/MySync.Updates.GitHub.csproj`) | Release source | `GitHubReleaseSource : IReleaseSource` (`api.github.com`, source-generated JSON), `DependencyInjection.AddGitHubReleaseSource(repo, version, token)`. Depends on Application only. |
| `MySync.SiteAssets` (`libs/mysync/site-assets/MySync.SiteAssets.csproj`) | Publication page assets | `EmbeddedSiteAssetSource : ISiteAssetSource` (manifest resources `site/…` from `dist/apps/mysync-site`, `root/…` from `Root/`), `DependencyInjection.AddSiteAssets()`. Depends on Application only. |
| `MySync.Tui` (`libs/mysync/tui/MySync.Tui.csproj`) | The terminal interface | `State` (`TuiState`, `ListModel`, `StatusSummary`), `Messages`, `Update` (`TuiUpdate`), `Effects`, `Views` (`Screens`), `Rendering` (`Frame`, `Theme`, `Symbols`, `Text`), `Terminal` (`ITerminal`, `AnsiTerminal`, `SystemConsole`, `TerminalDetection`), `Runtime` (`TuiRuntime`). No package. Depends on Application only. |
| `MySync.Storage.Azure` (`libs/mysync/storage-azure/MySync.Storage.Azure.csproj`) | Azure Blob provider | `AzureBlobShareStore : IShareStore`, `AzureAliasStore : IAliasStore`, `AzureShareContainer` (the container both use), `AzureStorageOptions`, `AzureStaticWebsitePublishTarget : IPublishTarget` + `PublishStorageOptions` + `AzureCopyLimits`, `AzureErrorMapper` (`RequestFailedException` → `StorageErrors`), `ConnectionStringRedactor`, `DependencyInjection.AddAzureBlobStorage()` / `AddAzureStaticWebsitePublishing()`. |
| `MySync.Storage.S3` (`libs/mysync/storage-s3/MySync.Storage.S3.csproj`) | AWS S3 provider | `S3ShareStore : IShareStore`, `S3AliasStore : IAliasStore`, `S3PublishTarget : IPublishTarget` + `AnonymousProbe`, `S3Connection` + `S3Bucket` (settings offline, credentials once), `S3Uploader` + `S3PartSizes` + `S3Limits`, `S3ErrorMapper`, `S3Redactor`, `S3StorageOptions`, `S3PublishOptions`, `DependencyInjection.AddS3Storage()` / `AddS3Publishing()`. |
| `MySync.Cli` (`apps/mysync/MySync.Cli.csproj`) | Presentation / composition root | `Program.cs`, `Commands/`, `Rendering/`, `ExitCodes.cs`. Selects the providers from `MYSYNC_PROVIDER` and `MYSYNC_PUBLISH_PROVIDER`. |

### Where abstractions live (critical)

**All abstractions are defined in `MySync.Application/Abstractions/`, implemented in Infrastructure or a storage project.** Application code depends on interfaces only and never references a concrete type from an outer layer.

| Abstraction | Folder | Implemented by |
|---|---|---|
| `IShareStore` (+ `ManifestSnapshot`, `StoredObjectInfo`) | `Abstractions/Storage/` | `MySync.Storage.Azure` `AzureBlobShareStore`, `MySync.Storage.S3` `S3ShareStore` |
| `IAliasStore` (+ `AliasSnapshot`) | `Abstractions/Storage/` | `MySync.Storage.Azure` `AzureAliasStore`, `MySync.Storage.S3` `S3AliasStore` |
| `IManifestSerializer` | `Abstractions/Storage/` | Infrastructure (shared by every provider, so manifest JSON is identical everywhere) |
| `ILocalFolder`, `IFileScanner`, `LocalFile`, `ScannedFolder` | `Abstractions/FileSystem/` | Infrastructure |
| `ILocalFolderFactory` (`OpenPullDestination`) and `ILocalFolder.GetEntryKindAsync` (+ `LocalEntryKind`) | `Abstractions/FileSystem/` | Infrastructure `LocalFolderFactory` (pull: the same `LocalFolder` and `PathResolver` rules for any root; case sensitivity found without writing) |
| `ICloneFolders` (+ `CloneFolderProbe`, `CloneFolderKind`) | `Abstractions/FileSystem/` | Infrastructure `CloneFolders` (`clone`'s target: what is there, create with parents, delete artifacts and emptied folders) |
| `ICloneRunner` | `Abstractions/Composition/` | the CLI's `FolderCloneRunner` (composition root; the tests link it in) |
| `IInterfaceLock` | `Abstractions/State/` | Infrastructure `InterfaceLock` (`.mysynctool.ui`, held exclusively with the PID inside) |
| `ILocalSources` (+ `LocalSourceInfo`, `SourceListing`, `OpenedSourceFile`, `SyncedFolder`) | `Abstractions/FileSystem/` | Infrastructure `LocalSources` (push: any folder, no share root, never writes) |
| `IPushRecordSerializer` | `Abstractions/Storage/` | Infrastructure `PushRecordSerializer` (`_mysync-push.json`) |
| `IContentHasher` | `Abstractions/Hashing/` | Infrastructure |
| `IShareStateStore`, `IFolderLock` | `Abstractions/State/` | Infrastructure |
| `IConflictLog` | `Abstractions/Logging/` | Infrastructure |
| `IIgnoreRulesLoader` (`LoadAsync`: the root's `.mysyncignore` and `.gitignore`; `Compile(text)` for the share's ignore files read from HEAD), `IIgnoreMatcher`, `UnionIgnoreMatcher` | `Abstractions/Ignore/` | Infrastructure (`UnionIgnoreMatcher` is in Application: both the loader and push use it) |
| `IMachineIdentity` (`Name` = `user@MACHINE`, plus `HostName`, `UserName`, `OperatingSystem`, `MysyncVersion` for push) | `Abstractions/Environment/` | Infrastructure |
| `ISyncProgressReporter`, `NullSyncProgressReporter` | `Abstractions/Progress/` | `apps/mysync/Rendering/SyncProgressDisplay` |
| `IReleaseSource` (+ `ReleaseInfo`, `ReleaseAsset`) | `Abstractions/Updates/` | `MySync.Updates.GitHub` (GitHub), Infrastructure `DirectoryReleaseSource` (a local folder) |
| `IExecutableInstaller`, `IUpdateWorkspace` | `Abstractions/Updates/` | Infrastructure `ExecutableInstaller` |
| `ISiteAssetSource` (+ `SiteAsset`) | `Abstractions/Publishing/` | `MySync.SiteAssets` `EmbeddedSiteAssetSource` |
| `IPublishTarget` (+ `PublishLocation`, `PublishedBlob`, `CopyMethod`, `CopySource`, `NativeObjectReference`) | `Abstractions/Publishing/` | `MySync.Storage.Azure` `AzureStaticWebsitePublishTarget`, `MySync.Storage.S3` `S3PublishTarget`; the CLI's `UnconfiguredPublishTarget` for an unknown `MYSYNC_PUBLISH_PROVIDER` |
| `IPublicationVerifier`, `ISiteDataSerializer` | `Abstractions/Publishing/` | Infrastructure `HttpPublicationVerifier`, `SiteDataSerializer` |
| `IPublicationRegistrySerializer` | `Abstractions/Storage/` | Infrastructure (shared by every provider, like the manifest's) |

Need a new external capability? Define the interface in `Abstractions/`, implement it outward, and register it in that project's `DependencyInjection.cs`.

### CQRS: command / query handler structure

There is **no MediatR**. Dispatch is a small set of generic interfaces in `MySync.Application/Abstractions/Messaging`:

```csharp
public interface ICommand;
public interface ICommand<TResponse>;
public interface IQuery<TResponse>;

public interface ICommandHandler<in TCommand> where TCommand : ICommand
{ Task<Result> Handle(TCommand command, CancellationToken ct); }

public interface ICommandHandler<in TCommand, TResponse> where TCommand : ICommand<TResponse>
{ Task<Result<TResponse>> Handle(TCommand command, CancellationToken ct); }

public interface IQueryHandler<in TQuery, TResponse> where TQuery : IQuery<TResponse>
{ Task<Result<TResponse>> Handle(TQuery query, CancellationToken ct); }
```

- **Commands mutate; queries only read.** A query never writes, including `.mysynctool`. So `status` doesn't persist hash-cache updates; the next `sync` does.
- **Every handler returns `Result` / `Result<T>`** and never throws for expected failures. Use the domain `*Errors` factories. Don't construct ad-hoc `Error`s in handlers.
- Handlers are `internal sealed`, use **primary constructors**, and thread the `CancellationToken` through every async call. Only `OperationCanceledException` from the caller's own token propagates.
- Handlers are auto-registered by Scrutor assembly scanning in `MySync.Application/DependencyInjection.cs`. Don't register them by hand.

### Vertical slices

Group by feature, then by use case:

```text
libs/mysync/application/
  Abstractions/{Messaging,Behaviors,Storage,FileSystem,Hashing,State,Logging,Ignore,Environment,Progress}/
  Shares/
    ShareFolder     "this folder is a share and the environment points at its storage" (status, info, resolve, gc)
    ShareResolver   ShareReference → ShareId (a name through its alias; NameNotFound); ShareNames frees a share's names (fresh read + If-Match)
    Preview/        PreviewNewShareQuery(.cs|Handler|Validator), NewSharePreview       # counts + size, no hashing
    Create/         CreateShareCommand(.cs|Handler|Validator), CreateShareResponse
    Clone/          CloneShareCommand(.cs|Handler|Validator), CloneShareResponse            # `sync <share>` in an empty folder
    CloneInto/      CloneIntoFolderCommand(.cs|Handler|Validator), CloneIntoFolderResponse, CloneCleanUp   # `clone <share> [<folder>]`
    GetInfo/        GetShareInfoQuery(.cs|Handler), ShareInfoResponse                  # also from elsewhere: info <name|id>
    Rename/         RenameShareCommand(.cs|Handler|Validator), RenameShareResponse     # `rename`: claim the new name, free the old
    Compression/    SetCompressionCommand, GetCompressionQuery, CompressionResponse    # `compression [on|off]`: a new revision, no object touched
    List/           ListSharesQuery(.cs|Handler), ShareListResponse                    # `shares`: the alias listing only
    Delete/         DeleteShareCommand(.cs|Handler|Validator), DeleteShareResponse     # `delete`: marker, sweep, local state
  Sync/
    Run/            RunSyncCommand(.cs|Handler|Validator), SyncReport
    Plan/           GetSyncPlanQuery(.cs|Handler) → SyncReport (DryRun)                # `status`: SyncPipeline.PreviewAsync
    Pipeline/       SyncPipeline, RemoteCommitter (412 retry loop), ObjectTransfers,   # internal, shared by Run/Clone/Resolve
                    ObjectCodec (the one encoder/decoder: OpenDecodedAsync, PrepareUploadAsync),
                    LocalApplier (phase 8), ConflictTracker, ProgressReportingStream
    Pull/           PullCommand(+Handler, Validator), PullReport                       # `pull` (§10c): read-only
    Push/           PushCommand(+Handler, Validator), PushReport; PushFiles (capture + upload, growing files); PushSelection (filters)  # `push` (§10b)
    SyncOptions     max parallelism (MYSYNC_PARALLELISM), commit attempts (push: MaxPushCommitAttempts = 20), back-off base and cap, gzip level (MYSYNC_COMPRESSION_LEVEL)
  Conflicts/
    List/           ListConflictsQuery(.cs|Handler), ConflictListResponse
    Diff/           DiffQuery(.cs|Handler|Validator), PrintVersionQuery, DiffSources       # `diff`: read-only
    Resolve/        ResolveConflictsCommand(.cs|Handler|Validator) → SyncReport (+ Resolved)
  Maintenance/
    CollectGarbage/ CollectGarbageCommand(.cs|Handler|Validator), GarbageReport
    Update/         UpdateCommand(.cs|Handler|Validator), UpdateReport, UpdateOptions (trusted key)
  Publishing/
    Publish/        PublishShareCommand(.cs|Handler|Validator), PublishReport           # `publish` (§10a)
    Unpublish/      UnpublishCommand(.cs|Handler|Validator), UnpublishReport
    List/           ListPublicationsQuery(.cs|Handler|Validator), PublicationListResponse
    PublishingShare, PublicationRegistryUpdater (ETag retry), PublicationRemover (index.html first; also used by `delete`)
  Session/          GetFolderStatusQuery, FolderStatus (+ FolderEntry, FileStatus, FolderStatusMapper), IFolderSession / FolderSession,  # `mysync tui` (§10d)
                    SessionEvents (events, SessionEventQueue, SessionProgress), InitializeEmptyShareCommand
  Diagnostics/
    SelfCheck/      RunSelfCheckQuery(.cs|Handler), SelfCheckResponse                  # `self-check`, release smoke test (§8, §16)
    SiteAssets/     ListSiteAssetsQuery(.cs|Handler), SiteAssetsResponse               # hidden `debug site-assets` (§16)
```

The matching command lives in `apps/mysync/Commands/<Feature>Commands.cs`.

### Cross-cutting concerns are behaviors (decorators)

Validation and logging are **not** written inside handlers. They're decorators in `Abstractions/Behaviors`, applied with Scrutor's `Decorate(...)` in `MySync.Application/DependencyInjection.cs`:

- `ValidationDecorator` runs every FluentValidation `IValidator<TCommand>` (and `IValidator<TQuery>`) before the handler. On failure it returns `Result.Failure(ValidationError)` without calling the handler. Validators cover argument shape: share ID is a UUID, `--keep` is `local|remote`, `resolve` has paths or `--all` (not both), `--parallel` is 1–64, and so on.
- `LoggingDecorator` logs start/finish of each command and query and pushes the `Error` onto the Serilog `LogContext` on failure. **It must never log request properties that could hold secrets.**

Decorator registration order defines wrapping order. New concerns (timing, metrics) become new decorators, never inline code.

### Result and errors

- `SharedKernel.Error` has `Code`, `Description` and `ErrorType` (`Failure`, `Validation`, `Problem`, `NotFound`, `Conflict`).
- Expected errors are static factories on per-concept `*Errors` classes in `MySync.Domain`, e.g. `ShareErrors.NotFound(shareId)`, `ShareErrors.FolderNotEmpty(path)`, `ShareErrors.AlreadyAShare(existingId)`, `SyncErrors.MassDeleteBlocked(count, total)`, `SyncErrors.CommitRetriesExhausted(attempts)`, `StateErrors.Corrupt(path)`, `StateErrors.FolderLocked`, `StorageErrors.Unauthorized`, `StorageErrors.Unreachable`, `ConfigurationErrors.Missing("AZURE_STORAGE_CONNECTION_STRING")`.
- **Partial outcomes are successes.** A sync that finished with open conflicts or skipped paths returns `Result<SyncReport>.Success`, and the report carries the conflicts and skips. The CLI turns that into exit code `3` or `9` (§10).
- No `Error.Description` ever contains a connection string, account key or SAS token.

---

## 4. `apps/mysync` — the CLI host

`MySync.Cli` (namespace `MySync.Cli`, assembly `mysync`) is built with **ConsoleAppFramework**, hosted on **`Microsoft.Extensions.Hosting`**, and everything it puts on screen goes through **Spectre.Console**.

### Host setup (`Program.cs`)

- **Use `Host.CreateEmptyApplicationBuilder(...)`, not `CreateApplicationBuilder`.** The default builder adds `appsettings.json` and user-secret sources from the content root, and for a CLI the content root is **the folder being synced**. A user's `appsettings.json` would then silently configure mysync. Add **only** `AddEnvironmentVariables()`.
- Logging is **Serilog writing to stderr**. Default level is `Warning`, `--verbose` → `Debug`, `--quiet` → `Error`. Nothing logs to stdout, because stdout carries `--json` output.
- Register `AddApplication()`, `AddInfrastructure()`, then the provider chosen by `MYSYNC_PROVIDER` (`azure-blob` → `AddAzureBlobStorage()`, `s3` → `AddS3Storage()`) and the publish target chosen by `MYSYNC_PUBLISH_PROVIDER` (default: the same; `AddAzureStaticWebsitePublishing()` / `AddS3Publishing()`). An unknown provider is a configuration error (exit `2`), reported when a command needs storage, not at startup. **`MYSYNC_PROVIDER` leads:** it decides which variables are read and required, and the other provider's are ignored (only the chosen provider's options are even registered), so both sets can sit in one environment. `AddInfrastructure()` takes the share root (`ShareRoot`) from the current directory unless one is registered first (tests register a temp folder).
- Commands are registered with `consoleApp.Add<T>()`. Global options (`--verbose`, `--quiet`, `--json`, `--parallel`) use ConsoleAppFramework's global-options support.
- **Don't use `builder.ToConsoleAppBuilder()`.** As soon as `ConfigureGlobalOptions` is called, ConsoleAppFramework builds its own (empty) service provider after parsing and replaces the host's, so no handler resolves. Instead `ConsoleApp.Create().ConfigureContainer(new HostServiceProviderFactory(builder))`: ConsoleAppFramework then builds the host itself, after global options are parsed, and the parsed `GlobalOptions` is registered into the same container via `ConfigureServices`.
- `--verbose` / `--quiet` set a Serilog `LoggingLevelSwitch` from inside the `ConfigureGlobalOptions` callback. Write that lambda's parameter type out (`(ref ConsoleApp.GlobalOptionsBuilder options) => …`): with the inferred C# 14 form `(ref options)`, the ConsoleAppFramework 5.7.13 generator fails with a NullReferenceException (CS8785).
- Ctrl+C cancels the token passed to every handler. The process exits `130` (ConsoleAppFramework maps `OperationCanceledException`). Nothing half-done is committed; uploaded objects of an interrupted *create* stay in the container without a manifest.
- `CliEnvironment` parses `MYSYNC_PROVIDER` and `MYSYNC_PARALLELISM` once at startup and registers `SyncOptions`; a bad value is reported (exit `2`) by the first command that needs storage, so `--help` still works.
- **Usage errors exit `2`**, not ConsoleAppFramework's defaults: `KnownCommands` rejects an unknown command before routing (otherwise it prints the help and exits `0`), and `ConsoleApp.LogError` writes to **stderr** (its default is stdout) and marks the run as a usage error. That only works because every command catches its own exceptions through `CommandRunner`; keep it that way.
- `ConsoleOutput.Configure()` stops Spectre wrapping at 80 columns when stdout or stderr is redirected, so a long path never splits across lines in a pipe or a log.

### `apps/mysync/Commands/`

| File | Commands |
|---|---|
| `CommandRunner.cs` | Not a command: scope per handler, `CheckStorage()` (settings before any work or prompt), failures to stderr (and one JSON document with `--json`), unexpected exceptions → exit `1`, redacted, stack trace only with `--verbose` |
| `SyncCommands.cs` | `sync [<name|shareId>] [--name <name>] [--compress]`, `status` |
| `CloneCommands.cs` | `clone <name|shareId> [<folder>]` |
| `PullCommands.cs` | `pull <name|shareId> [<share-path>...] [--to] [--latest] [--host]... [--overwrite] [-n]` |
| `PushCommands.cs` | `push <name|shareId> [<path>...] [--updated-since] [--include]... [--exclude]... [--max-size] [--host] [--create] [--compress] [--no-info] [-n]` |
| `ConflictCommands.cs` | `conflicts`, `diff` (its `--print` combinations are checked here; raw bytes to stdout), `resolve` |
| `ShareCommands.cs` | `info`, `delete`, `shares`, `rename`, `compression` |
| `MaintenanceCommands.cs` | `gc` |
| `TuiCommands.cs` | `tui [--name] [--compress] [--ascii] [--no-color]`: the start-up checks, then `MySync.Tui`'s runtime; no screen code |

- **Keep commands thin:** resolve the handler from a scope, invoke it, render the result, return the exit code.
- `sync` with no argument and no `.mysynctool` is the **create** flow. The command first runs `PreviewNewShareQuery` (file count and size, no hashing), asks for confirmation if needed (§9), then runs `CreateShareCommand`.
- **Confirmation lives in the command, never in a handler.** `--yes` never reaches the Application layer. A non-interactive stdin without `--yes` above the thresholds fails instead of assuming consent.
- **A command given all its arguments never prompts.** No credentials are ever prompted. The only prompt in the whole tool is the large-share `y/N` (more than `SyncCommands.LargeFileCount` = 1,000 files or `LargeTotalBytes` = 1 GB), on stderr. Declining exits `130` with nothing uploaded. Without a terminal (or with `--json`) and without `--yes` it fails with `Cli.ConfirmationRequired` (exit `2`). An unsafe root is refused *before* asking.
- `sync --dry-run` in a folder that isn't a share previews the create (count and size, nothing uploaded). `--dry-run` with a share ID is a usage error: cloning only downloads.

### `apps/mysync/Rendering/` — everything the user sees

| Piece | What it is |
|---|---|
| `ConsoleOutput` | The one way to write: `Fail` / `Warn` / `Success`, `Value` / `Muted` / `Label` (escaped), byte and count formatting, `IsInteractive`, `CanRedraw` |
| `SyncProgressDisplay` | `ISyncProgressReporter` for `sync`, `clone` and `resolve`, **on stderr**: live bars when stderr can redraw, one line per phase and planned transfer otherwise, nothing with `--quiet` / `--json` |
| `DiffView`, `TerminalText`, `ShellQuote` | `diff` output (colour on a terminal, a plain patch otherwise), untrusted text made safe for the screen, paths quoted for pasting |
| `SyncReportView` | End-of-run summary: transferred, deleted, **conflicts**, skipped (with reasons); `RenderConflicts` is also the `conflicts` table (path, kind, local vs remote hash/size/mtime, remote `updatedBy` and revision); `RenderResolve` for `resolve` |
| `PlanView` | `status` / `--dry-run` output |
| `PullReportView` | `pull`: what came from which revision, `into:` each landing folder, already-there and replaced counts, warnings and skipped paths (stderr) |
| `PushReportView` | `push`: what went where, the filter line (cutoff, files left out by reason), warnings and skipped paths (stderr), how to get the files |
| `ShareInfoView` | `info` field list |
| `GarbageView` | `gc`: what was (or would be) deleted, and what was kept and why |
| `JsonOutput` | `--json`: one JSON document on stdout per command, source-generated serializer |

- **Escape everything user-supplied** before it goes into markup (`ConsoleOutput.Value`, or `Markup.Escape`). A file path containing `[` would otherwise be read as a style tag and vanish or throw. Synced paths are user-supplied by definition.
- **Failures go to stderr** through `ConsoleOutput.Fail`, which returns the exit code so a command can `return ConsoleOutput.Fail(error)`. Never write failures with `AnsiConsole` (stdout).
- **Two levels of "is there a human here":** `IsInteractive` means stdin can be read (the `y/N` prompt is safe). `CanRedraw` also requires ANSI support, which live progress bars need. Check `CanRedraw` before showing a bar. Without it, print line-oriented progress or nothing, so piped output doesn't fill with escape codes.
- **Progress is carried on the command, not injected.** `RunSyncCommand.Progress` (and the clone and resolve equivalents) is an optional `ISyncProgressReporter`. The command supplies a `SyncProgressDisplay` for the length of one `AnsiConsole.Progress()` block, and handlers fall back to `NullSyncProgressReporter`. A display belongs to one invocation. Don't make it a DI service with mutable state.
- **Progress is measured in bytes, counted where they leave or reach the file** (a `ProgressReportingStream` around the `FileStream`). The upload stream **must stay seekable and report `Length`**: the Azure SDK needs both to retry a failed block.

---

## 5. Configuration — environment variables only

| Variable | Required | Meaning |
|---|---|---|
| `AZURE_STORAGE_CONNECTION_STRING` | with `azure-blob` | Storage account connection string. |
| `MYSYNC_CONTAINER` | no | Blob container name. Default `mysync`. Azure only. |
| `MYSYNC_PROVIDER` | no | `azure-blob` (default) or `s3`. It decides which of the variables below are read and required; the other provider's are ignored. |
| `MYSYNC_S3_BUCKET` | with `s3` | The share bucket (must exist; never created). No dots in the name. |
| `MYSYNC_S3_REGION` | with `s3`, else `AWS_REGION` | The bucket's region. |
| `AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY`, `AWS_SESSION_TOKEN`, `AWS_PROFILE`, `AWS_WEB_IDENTITY_TOKEN_FILE`, `AWS_EC2_METADATA_DISABLED`, … | with `s3` | The standard AWS credential chain, in the `aws` CLI's order: keys in the environment; the `AWS_PROFILE` (or `default`) profile of any kind, SSO and assumed roles included; web identity; ECS; the EC2 instance role. Read by the SDK, never stored. |
| `MYSYNC_PUBLISH_PROVIDER` | no | Where publications go: `azure-blob` or `s3`. Default: `MYSYNC_PROVIDER`. Mixed providers stream every file. |
| `MYSYNC_PUBLISH_S3_BUCKET` | for S3 publishing | The public publishing bucket (§10a). No dots. |
| `MYSYNC_PUBLISH_S3_REGION` | no | Its region. Default: the share's (`MYSYNC_S3_REGION`, then `AWS_REGION`). |
| `MYSYNC_PUBLISH_AWS_PROFILE` | no | A credential profile for publishing only; copies are then streamed (that identity may not read the share bucket). |
| `MYSYNC_PARALLELISM` | no | Max concurrent transfers. Default `min(ProcessorCount * 2, 16)`. `--parallel` overrides it. |
| `MYSYNC_COMPRESSION_LEVEL` | no | gzip level for uploads to a share with compression on: `fastest` (default) or `optimal`. Anything else is `ConfigurationErrors.Invalid` (exit `2`). |
| `MYSYNC_PUBLISH_CONNECTION_STRING` | for `publish`, `unpublish`, and `delete` of a share that has publications | The **publishing** account (static website hosting on). May be the private account (`publish` warns); a separate one is better. A secret, like the other connection string. |
| `MYSYNC_PUBLISH_BASE_URL` | same | The public URL of that account's static website, absolute `https` (e.g. `https://<account>.z13.web.core.windows.net`, or a custom domain); on S3 the bucket's REST address `https://<bucket>.s3.<region>.amazonaws.com` or CloudFront in front of it (the website endpoint is http only). A connection string can't tell it, and mysync uses no management credentials. |
| `MYSYNC_UPDATE_SOURCE`, `GITHUB_TOKEN` | no | `update` only (§10). |

- Options bind from environment variables into `AzureStorageOptions` / `PublishStorageOptions` / `SyncOptions`. They're **not validated at startup**, so `mysync --help` and `--version` work with nothing configured. A missing variable surfaces as `ConfigurationErrors.Missing(name)` → exit `2`, with a one-line message naming the variable. A malformed connection string or an invalid container name is `ConfigurationErrors.Invalid(name, reason)` (also exit `2`); the reason never contains the connection string. The Azure store reads its settings on first use, so the error comes from whichever call needs storage.
- **Never prompt** for any credential or setting.
- **Never print, log or store the connection string or account key**, including in exceptions, `--verbose` output, `--json` output and `.mysynctool`. `ConnectionStringRedactor` (`AccountKey=***`, `SharedAccessSignature=***`) is applied to anything from the Azure SDK that might echo it.
- `.mysynctool` records the **provider, account name and container** (S3: `s3`, the region, the bucket), never secrets. If the current environment points at a different account or container than the one recorded, fail (`StateErrors.StorageMismatch`) rather than syncing against the wrong place.
- The container is created (`CreateIfNotExists`) only by the **create-share** flow.

---

## 6. Remote storage layout (content-addressed)

Everything for a share lives under one prefix in the container. Key building is `MySync.Domain.Storage.ShareLayout`, the one place blob names are composed:

```text
<container>/
├── aliases/<name>                          # a share's name → its share ID (§6a); the only record of names
└── shares/<shareId>/
    ├── manifest.json                       # HEAD: the current state of the share (single commit point)
    ├── manifests/<revision:D10>.json       # immutable history snapshots (best-effort, for recovery + GC safety)
    ├── publications.json                   # the share's publication records (§10a), ETag-guarded
    └── objects/<h0h1>/<sha256-hex>         # file contents, named by SHA-256 of the content
```

- Blob names come from `ShareLayout.For(shareId)` (`.Manifest`, `.ManifestHistory(rev)`, `.ObjectBlob(hash)`), and `TryParseObject` / `TryParseManifestHistory` accept only canonical names, so a stray blob is never taken for an object. `ShareLayoutTests` pins the exact strings: changing them breaks every existing share.
- `shareId`: lowercase UUID, `Guid.NewGuid().ToString("D")`. `ShareId.TryParse` accepts only the 36-character form (no braces, no whitespace, not the nil UUID).
- Object name: lowercase hex SHA-256 of the **uncompressed file bytes**. The first two hex characters form the fan-out folder (`objects/ab/abcdef…`).
- Objects are **immutable**. An object blob is never overwritten or modified. Upload with `If-None-Match: *`; a `409/412` because it already exists counts as success (same name, same content).
- **Objects have an encoding, not an identity of their own.** An object's bytes are raw or gzip, and its standard `Content-Encoding` property says which (`gzip`, or absent). Its key is still the SHA-256 of the **uncompressed** content, and manifests, `.mysynctool`, the hash cache and the merge only ever see uncompressed hashes and sizes: there is no per-file compression flag. **One key, one form:** a compressed and a raw copy of the same content can't coexist; whoever uploads first decides, everyone else finds it stored and skips the upload, and every reader handles both. Objects are never recompressed or decompressed in place.
- **Compression is a setting of the share** (`manifest.json` `compression`, below), applied by every machine to what it uploads from then on (`mysync sync --name <n> --compress`, `mysync push <n> --create --compress`, `mysync compression on|off`). `CompressionPolicy` decides per file: off → raw; under 512 bytes → raw; an already-compressed type by extension (archives, images, audio/video, zip-based documents, `pdf`, `woff`) → raw; otherwise gzip if a sample of the first 1 MiB shrinks to 90% or less. gzip from the BCL (`GZipStream`), level `Fastest` (`MYSYNC_COMPRESSION_LEVEL=optimal` to change it).
- **The store verifies every upload's SHA-256 and stores nothing on a mismatch** (`StorageErrors.HashMismatch`). A file that changed while it was being read must never become an object under the wrong name: because objects are immutable and an existing object is never re-uploaded, a wrong one would stay wrong. (Azure: stage 4 MiB blocks while hashing, commit the block list with `If-None-Match: *` only if the hash and length match; uncommitted blocks are never visible and Azure drops them after about a week. Block IDs are unique per upload, so two machines uploading the same object at once can't mix blocks.) The pipeline treats a mismatch as "unstable", skips the path and retries it next run.
- Object blob metadata: `sha256=<hex>`, `size=<bytes>` (always the **uncompressed** size), and for a gzip object `storedsize=<bytes>` plus `Content-Encoding: gzip`. Content-Type `application/octet-stream`. An unknown `Content-Encoding` is `Storage.UnsupportedEncoding` ("upgrade mysync", exit `8`), never guessed. Manifests are `application/json`, written byte-for-byte as `IManifestSerializer` produced them.
- Identical files in a share are stored once. Renames cost nothing (only the manifest changes).
- Dedupe is **per share**, not across shares. Shares stay independent, which keeps GC and deletion trivial.
- `ShareLayout` is provider-agnostic: an S3 bucket holds exactly the keys an Azure container does. Keep it flexible enough to move to one container per share later (§19).

### Manifest (`manifest.json`)

```json
{
  "formatVersion": 1,
  "shareId": "0b6f3c1e-8f0a-4c43-9d3e-5a1f2b7c9e10",
  "revision": 17,
  "createdUtc": "2026-11-15T18:40:12Z",
  "updatedUtc": "2026-11-16T09:02:55Z",
  "updatedBy": "usman@DEV-LAPTOP",
  "entries": {
    "docs/readme.md":     { "type": "file", "sha256": "9f86d0…", "size": 1204, "mtimeUtc": "2026-11-15T18:31:02Z", "exec": false },
    "scripts/deploy.sh":  { "type": "file", "sha256": "2c26b4…", "size": 880,  "mtimeUtc": "2026-11-14T10:12:44Z", "exec": true  },
    "logs":               { "type": "dir" }
  }
}
```

- `entries` is keyed by **normalized relative path** (§8), sorted ordinally when serialized, so output is deterministic.
- `type: "dir"` entries exist **only for empty directories**. Non-empty directories are implied by file paths. `Manifest` drops a redundant `dir` entry rather than rejecting the manifest.
- `Manifest` rejects (`ShareErrors.InvalidManifest`) the same path twice (including NFD/NFC spellings of one name, which `SharePath` makes equal) and any entry "inside" a file (`a` as a file plus `a/b`). Timestamps are not cross-checked: they come from different machines' clocks.
- A higher revision only comes from `manifest.CreateNextRevision(...)`, so "+1 per commit" holds by construction.
- `revision` goes up by exactly 1 per commit.
- Readers **must refuse** a manifest with `formatVersion` higher than they support (`ShareErrors.UnsupportedFormat` → "upgrade mysync").
- **Format 3 is a live manifest of a share that has ever had compression on** (`Manifest.CompressionFormatVersion`, `MaxReadableFormatVersion = 3`), with a top-level `"compression": "gzip" | "none"` written just before `entries`. It becomes 3 when compression is first turned on (or the share is created with `--compress`) and **stays 3 for good**, `"none"` included, because compressed objects may still exist: a client that can't decompress must refuse the share ("upgrade mysync"), not fail on hashes. `compression` is required in format 3 and refused in 1 and 2. Every commit carries the setting and format forward (`CreateNextRevision`); only `WithCompression` changes the setting. The deleted marker stays format 2 whatever the share was. A share that never turns compression on is byte-for-byte unaffected (format 1, no `compression` field; `ManifestSerializerTests` pins all three shapes).
- **Format 1 is every ordinary manifest that never had compression; format 2 is only the deleted marker** (`Manifest.DeletedFormatVersion`), written by `mysync delete`: no entries, plus `"deleted": { "deletedUtc", "deletedBy" }`. Keep ordinary manifests at 1 so older clients keep working. The marker *must* stay above it: a client that predates `delete` would otherwise read "no entries" as "everything was deleted remotely" and delete its local copies (the mass-delete guard only catches > 10 files). A deleted manifest refuses `CreateNextRevision`, so nothing can be committed on top of it. `ManifestSerializerTests` pins the marker JSON.
- Plain JSON for v1, for easy debugging. If manifests get large (>~10 MB), gzip them in a later format version.
- Serialized only by `IManifestSerializer` (Infrastructure, source-generated `MySyncJsonContext`), so every provider writes byte-identical manifests.

### Commit protocol (the only way remote state changes)

Implemented by `RemoteCommitter` in `Application/Sync/Pipeline`:

1. Upload every object the new manifest references that doesn't already exist.
2. Write `manifest.json` **conditionally**:
   - New share: `If-None-Match: *`.
   - Existing share: `If-Match: <ETag read at the start of this sync>`.
3. On `StorageErrors.PreconditionFailed` (someone else committed first): re-read the manifest and re-run the merge with the new remote state. Objects already uploaded stay valid. Retry up to **5** times with jittered backoff, then `SyncErrors.CommitRetriesExhausted` → exit `5`.
4. After a successful commit, write `manifests/<revision>.json` (best-effort; a failure only logs a warning).

Files are never "half uploaded" from a reader's point of view. Readers see a new revision only after all its objects exist.

---

## 6a. Share names (`aliases/<name>`)

Every new share has a **required, unique, human-readable name** (`next-wave`), so people type `mysync sync next-wave` instead of a UUID. The share ID stays the storage key everywhere; a name only points at it.

- **The alias blob is the only record of names.** `aliases/<name>` in the share container (`ShareLayout.AliasesPrefix`, `ShareLayout.Alias(name)`), one block blob per name, `application/json`: a body `{ "shareId", "createdUtc", "createdBy" }` (for people and other providers) and **the same three values as metadata** (`mysyncshareid`, `mysynccreatedutc`, `mysynccreatedby`, the last URL-encoded because metadata is ASCII), written in **one** Put. Readers use the metadata. **Manifests and `.mysynctool` don't hold names**: no format change.
- **`ShareName`:** trimmed, lowercased (ASCII), 3–40 of `a–z0–9-`, starting and ending with a letter or digit, never UUID-shaped. Those rules are also what keeps the name a safe blob path segment. `ShareName.SuggestFrom(folderName)` makes the suggestion in the "name required" message (accents stripped via our NFD, the rest dashed); a share is never created with a name nobody chose.
- **`ShareReference`:** what a user types where a share is expected: a share ID if it parses as one, else a name. `ShareReference.Parse` is the one parser (CLI), and `ShareResolver` the one resolution (Application): a name goes through `IAliasStore.GetAsync`, a missing one is `NameNotFound`.
- **Uniqueness is the conditional create.** `IAliasStore.CreateAsync` uses `If-None-Match: *`; claiming a name *is* the check that it's free. Never read-then-write.
- **Create's order:** the name is checked first (missing → `NameRequired`, before the folder is even walked: the CLI asks `PreviewNewShareQuery(CountFiles: false)`), the container is made ready, then **the name is claimed before anything is scanned or uploaded**, then the usual create (upload, commit revision 1). A taken name is `NameTaken` naming the share that has it. If the create fails or is cancelled after the claim, the name stays and the share is **incomplete** (alias, no manifest): clone and info say `Incomplete`, `shares` lists it, and `delete <name> --yes` removes it.
- **Rename** (`RenameShareCommand`, in a share's folder): claim the new name, then free the share's other names. Interrupted in between, the share has two names, both resolving to it; the next rename or delete cleans up. The same name again is a no-op; a share without a name (made before names) gains one.
- **Delete frees names** (§10, `delete` step 3a), by the reverse lookup `IAliasStore.FindByShareAsync`: one listing of `aliases/` filtered by share ID, since manifests don't know their names. Deleting **frees** a name; there are no tombstones.
- **`shares`** (`ListSharesQuery`) is **one paged listing** of `aliases/` with the metadata trait (up to 5,000 per request), sorted by name. It reads no manifest. A damaged alias is reported with its name (`Storage.InvalidAlias`) and a stray blob under `aliases/` skipped (`Storage.StrayAlias`), as warnings.
- **Lifecycle rules:** an account-wide rule like "delete block blobs N days after creation" also deletes `aliases/<name>` (its creation date never changes): the name vanishes, stops resolving, and could be claimed by someone else, while the share may live on. Include or exclude `aliases/` deliberately.

---

## 7. Local state

### `.mysynctool` (file, in the share root)

Owned by `IShareStateStore` (`JsonShareStateStore`). JSON, written **atomically**: write `.mysynctool.new`, flush, `File.Move(..., overwrite: true)`.

```json
{
  "formatVersion": 1,
  "provider": "azure-blob",
  "accountName": "teamsyncstorage",
  "container": "mysync",
  "shareId": "0b6f3c1e-8f0a-4c43-9d3e-5a1f2b7c9e10",
  "baseRevision": 17,
  "base": { "docs/readme.md": { "type": "file", "sha256": "9f86d0…", "size": 1204, "mtimeUtc": "2026-11-15T18:31:02Z", "exec": false } },
  "hashCache": { "docs/readme.md": { "size": 1204, "mtimeTicksUtc": 638672..., "sha256": "9f86d0…" } },
  "conflicts": [
    {
      "path": "reports/q3.xlsx",
      "kind": "modify/modify",
      "base":   { "type": "file", "sha256": "aa11…", "size": 50211, "mtimeUtc": "2026-11-15T18:31:02Z", "exec": false },
      "local":  { "type": "file", "sha256": "bb22…", "size": 51007, "mtimeUtc": "2026-11-16T08:55:10Z", "exec": false },
      "remote": { "type": "file", "sha256": "cc33…", "size": 50980, "mtimeUtc": "2026-11-16T07:12:41Z", "exec": false },
      "remoteRevision": 18,
      "remoteUpdatedBy": "ali@SERVER01",
      "firstDetectedUtc": "2026-11-16T09:02:55Z",
      "lastSeenUtc": "2026-11-16T09:02:55Z"
    }
  ]
}
```

- `base` uses the same `ManifestEntry` type as the manifest (so it carries `mtimeUtc` too): after a commit the new base is literally the committed manifest's entries.
- `base` is the **last state this folder and the remote agreed on**. It drives the three-way merge (§9). Losing or corrupting it would make deletes look like additions, so treat it carefully.
- `hashCache` avoids re-hashing unchanged files. A cached hash is reused only if size **and** mtime match exactly. **Racy-mtime rule:** if a file's mtime is within 2 seconds of the moment it was hashed, don't store it in the cache (coarse-resolution filesystems such as FAT can hide a same-second edit).
- Each conflict side is a full entry (same shape as `base`), or `null` when that side has no entry (deleted). `remoteRevision` / `remoteUpdatedBy` are the HEAD the conflict was found against, so they're present even when `remote` is `null`. `JsonShareStateStoreTests` pins the exact JSON.
- An unusable `hashCache` entry is dropped (the file is hashed again); anything else that doesn't validate is `StateErrors.Corrupt`. A newer `formatVersion` is checked first, so it's `UnsupportedFormat` even if the rest changed shape.
- `conflicts` is rebuilt every sync from the merge plan. An entry disappears when the path is no longer in conflict. `kind` is one of `modify/modify`, `add/add`, `modify/delete`, `delete/modify`, `type`.
- If `.mysynctool` is missing but other files exist, `sync` creates a new share. `RunSyncCommand` itself refuses with `ShareErrors.NotAShare`; the CLI picks the flow from `PreviewNewShareQuery.ExistingShareId`. If it's unreadable or corrupt, **stop** (`StateErrors.Corrupt`). Never fall back to creating a new share or treating everything as new.
- The domain model is `MySync.Domain.State.ShareState`. The JSON shape lives only in Infrastructure.

### Other local artifacts (all always ignored, never synced)

Names are constants in `MySync.Domain.State.StateFileNames`.

| Path | Purpose |
|---|---|
| `.mysynctool` | state |
| `.mysynctool.new` | atomic-write temp |
| `.mysynctool.lock` | process lock (`IFolderLock`), held via `FileStream` with `FileShare.None` for the whole run. A second `mysync` in the same folder fails with `StateErrors.FolderLocked` → exit `6` |
| `.mysynctool.tmp/` | download staging directory (same volume, so the final move is atomic). Cleaned at start and end of every run |
| `.mysync-pull.tmp/` | `mysync pull`'s staging directory in the **destination** root (`StateFileNames.PullStagingDirectory`), emptied at the start and end of every pull. Always ignored, so a pull destination inside a share never syncs it |
| `.mysynctool.log` | append-only, human-readable log of conflicts detected and resolved (`IConflictLog`): `2026-11-16T09:02:55Z CONFLICT modify/modify reports/q3.xlsx local=bb22… remote=cc33… (rev 18, ali@SERVER01)` and `… RESOLVED keep-local reports/q3.xlsx`. Rotate at 5 MB (keep one `.mysynctool.log.1`). No secrets |
| `.mysynctool.ui` | `mysync tui`'s lock (`IInterfaceLock`): held exclusively while the interface runs, with its PID inside; a second `tui` → `Tui.AlreadyRunning` (exit `6`). A file left by a crash is taken over; removed on quit |

### `.mysyncignore` and `.gitignore`

- gitignore syntax, read from the share root only (v1), via `IIgnoreRulesLoader`. UTF-8 (BOM fine), LF or CRLF, at most 1 MB.
- **A `.gitignore` in the share root is read too** (`StateFileNames.GitIgnoreFile`; `StateFileNames.IgnoreFiles` lists both). Each file is compiled on its own and they are joined as a **union** (`UnionIgnoreMatcher`): a path is ignored if either file ignores it, so a `!` rule in one can't re-include what the other ignores. Either file alone works as before; neither means built-ins only. An unreadable or oversized one is `Local.Unreadable` naming it. Only the root's: a `.gitignore` in a subfolder is an ordinary file and not read (v1), unlike git.
- **Both are synced**, so the whole team shares the same rules. Reading `.gitignore` (new after 0.9.1) means a share that already holds files its `.gitignore` matches stops syncing them on upgraded machines: they are frozen locally and remote-only ones are reported as `IgnoredLocally`, never deleted.
- Built-in excludes, always applied: the artifacts above, `.DS_Store`, `Thumbs.db`, `desktop.ini`, `._*` (macOS resource forks; `StateFileNames.OsClutter` / `IsOsClutter`). They match a name **at any depth** (a nested share's `.mysynctool` must never sync) and **can't be re-included** with `!`.
- **The matcher is our own** (`Infrastructure/Ignore`: `IgnoreRule` compiles one line, `GitIgnoreMatcher` applies them). Both NuGet candidates disagreed with git on real cases (`Ignore` 0.2.1: `?`, `[!a]`, case, re-including under `/sub/*`; `MAB.DotIgnore` 3.0.2: `\!`, escaped trailing spaces, `!/keep/`, and it drags in the netstandard1.3 package graph). **git is the reference**: `GitIgnoreMatcherTests` runs every case in `gitignore-cases.txt` against `git` itself on each CI OS. When the matcher and git disagree, the matcher is wrong; add the case to the corpus.
- Semantics, as git: last matching rule wins; a path inside an ignored directory stays ignored whatever later rules say; `a/**` also matches the directory `a`; a malformed bracket expression matches nothing. **Case-sensitive on every OS**, so a rule means the same on every machine. Patterns are NFC-normalized like paths. Regexes are `NonBacktracking`, because the file comes from other machines and must not be able to make matching exponential.
- An ignored path is neither uploaded nor downloaded, and **not deleted locally** even if the remote has no such entry.
- If remote has an entry the local ignore rules match, skip it and show a warning in `status`. Don't download it.

---

## 8. Paths and cross-platform rules

Rules live in `MySync.Domain/Paths/` (`SharePath` value object, `PathRules`, `PathErrors`). The OS-facing side (`ILocalFolder`) lives in Infrastructure. Both need thorough unit tests run on all three OSes in CI.

- Manifest paths are **relative**, use `/`, have no leading `/` and no `.` or `..` segments, and are **Unicode NFC normalized**. macOS can return NFD names, so normalize on scan and when mapping manifest → local path.
- **Validate every path read from a remote manifest** before touching disk. Reject absolute paths, `..`, drive letters, NUL and control characters (C0, DEL and C1), unpaired surrogates, and **backslashes** (Windows treats `\` as a separator, so `a\..\..\x` would escape the root there; a Linux file with `\` in its name is skipped with a warning). Paths in error messages go through `PathErrors.Display`, which writes control characters as `\uXXXX`, so a crafted name can't send escape sequences to the terminal. Make sure the resolved full path stays inside the share root (defence against a crafted manifest). A `SharePath` can only be constructed through this validation.
- **Case collisions:** if the manifest has two entries that differ only in case (`Foo.txt` / `foo.txt`), then on case-insensitive filesystems (Windows, default macOS) download neither. Warn and list them. On Linux they're fine. When creating a share or uploading, warn if the local tree contains case-only collisions.
- **Windows-invalid names** (`<>:"|?*`, trailing dot/space, reserved names `CON`, `PRN`, `AUX`, `NUL`, `COM1-9`, `LPT1-9`): on Windows, skip those entries with a warning. Never fail the whole sync.
- Long paths: use the full paths .NET produces. Document that Windows needs long-path support enabled for >260 chars.
- **Symlinks and junctions:** don't follow and don't sync. Skip with a warning (v1). `PathResolver` also refuses any path that goes *through* a symlink or junction (`escape/planted.txt` where `escape` links outside the root), and the scanner never enters one.
- **Mapping a `SharePath` to disk** (`PathResolver`): if the NFC spelling isn't on disk, each segment is matched against the directory's entries by their NFC form, so an NFD name written by a Mac tool is found, not duplicated. On Windows a name Windows can't hold is refused there too (`a:b` would be an alternate data stream). The final path must be inside the root.
- **Scanner details** (`FileScanner`): mysync's own artifacts in the root are skipped whatever the matcher says; two names in one directory that are equal after NFC (possible on Linux and NTFS) keep the first and skip the other as `InvalidName`; an unreadable directory is skipped as `Unreadable` (the pipeline then freezes everything under it, §9); a directory whose entries are all ignored counts as empty; an empty file is never opened (its hash is known, and FIFOs, sockets and devices report size 0, so they can't block a read). Hashing runs in parallel; results are sorted.
- **Removing directories:** `DeleteDirectoryIfEmpty` treats a directory that holds only OS clutter as empty and deletes the clutter with it, so browsing a folder in Finder doesn't keep a remote delete from ever applying.
- **Executable bit:** on Unix, record `exec: true` if any execute bit is set; on download, apply `u+x` (`File.SetUnixFileMode`). The bit **is part of the merge comparison** (§9), so a `chmod +x` (or `-x`) alone syncs. Applying an exec-only change transfers no bytes: locally it only changes the file mode, remotely only the manifest entry (the object already exists). On Windows the scanner can't see the bit, so it takes `exec` from the **base entry** for existing paths and uses `false` for new ones. A Windows machine then never reports an exec change of its own, a Windows edit doesn't strip the bit from a Linux script, and a remote exec change is a no-op on disk there.
- **mtime:** after downloading, set the local file's last-write time to the manifest `mtimeUtc`, then record it in `hashCache`.
- Content is synced byte-for-byte. **No line-ending conversion, ever.**
- Relative paths are normalized to forward slashes in `FileScanner`, and scan results are sorted ordinally, so a share created on Windows reads the same everywhere and runs are reproducible.

### Unicode normalization (NFC) is our own code

**Never call `string.Normalize` / `IsNormalized`.** Release builds use `InvariantGlobalization=true` (§16), and in invariant mode .NET's normalization silently returns NFD input unchanged (no exception). Checked 2026-11-16 in published binaries on `osx-arm64`, `linux-x64`/`linux-arm64` (`ubuntu:24.04`) and `linux-musl-x64`/`linux-musl-arm64` (`alpine:3.22`). Turning invariant mode off would need libicu on every Linux machine (the binary aborts at startup without it), and Microsoft's app-local ICU package has no macOS or musl builds. So we keep invariant mode and ship our own NFC.

- `MySync.Domain.Paths.Normalization.NfcNormalizer` implements UAX #15 composition (decompose → canonical ordering → compose, Hangul arithmetically). `PathRules.ToNfc` is the only public entry point. Fast path: a string with every char below U+0300 is returned as-is (pinned by a test). Unpaired surrogates pass through unchanged.
- Its tables, `UnicodeNormalizationData.g.cs`, are **generated**, never hand-edited: `nx run mysync-domain:generate-unicode` (or `dotnet run tools/unicode/GenerateNfcData.cs -- 17.0.0`) downloads the UCD for that version (`UnicodeData.txt`, `DerivedNormalizationProps.txt`) and also refreshes the gzipped `NormalizationTest.txt` in `tests/mysync/domain-unit-tests/Paths/Normalization/`. Output is deterministic. Commit both files together.
- **Pinned to Unicode 17.0.0.** Bump only deliberately: a newer Unicode version can map a name to a different manifest key, so every client in a team must run the same version. Treat it as a breaking change and note it in the release notes.
- `NfcNormalizerTests` runs the full Unicode conformance file (every NFC invariant, plus "every code point not in Part 1 is unchanged") on all three CI OSes. `mysync self-check` checks normalization inside the published binary, and the release smoke tests run it (§16).

---

## 9. Sync algorithm

`SyncPipeline` (`Application/Sync/Pipeline`) sequences the phases. The merge decision is `MySync.Domain.Sync.MergePlanner`, a **pure function** with no I/O, so it can be exhaustively unit-tested.

### Phases of `mysync sync` in an existing share (`RunSyncCommand`)

1. **Lock and load:** take `.mysynctool.lock`, load and validate `.mysynctool`, check the environment's account/container match, clean `.mysynctool.tmp/`.
2. **Scan local:** walk the tree (skip ignored paths and symlinks), compute SHA-256 using `hashCache`. After hashing each file, re-stat it. If size or mtime changed during hashing, mark it **unstable**, leave it out of this run and warn.
3. **Read remote:** download `manifest.json` and remember its ETag.
4. **Plan:** `MergePlanner.Plan(base, local, remote)` → `SyncPlan` (table below).
5. **Safety guards** (`MassDeleteGuard`, fail with `SyncErrors.MassDeleteBlocked` → exit `7` unless `--allow-mass-delete`):
   - Both thresholds are strict: deleting 11 of 22 (exactly half) or 10 of 10 passes. Empty directories don't count as local files. `--no-delete` plans delete nothing, so they never trip it.
   - The plan deletes > 50% of local files (and > 10 files).
   - The plan deletes > 50% of remote entries (and > 10). This protects against an unmounted drive or wrong folder making everything look deleted.
6. **Upload** objects needed by local-wins actions (parallel, skip if the object exists in either form), compressed as the share's setting **in this attempt's HEAD** and `CompressionPolicy` say (`ObjectCodec.PrepareUploadAsync`). A gzip upload streams file → SHA-256 → gzip → store in one pass; the store asks for that verdict before committing the block list, so a file that changed while it was read is never stored under the old hash (it's skipped as `Unstable`). If another machine turns compression on mid-run, the commit retry re-reads HEAD and carries the setting forward; objects already uploaded raw are fine.
7. **Commit** the new manifest = remote manifest + local-wins changes applied (protocol §6). On precondition failure → back to phase 3.
8. **Apply remote-wins actions locally:**
   - Download to `.mysynctool.tmp/` through `ObjectCodec.OpenDecodedAsync` (decoded exactly once), **verify the SHA-256 of the decoded bytes while streaming** (mismatch → discard and error for that path), then move it into place (overwrite). Decoding stops as soon as it passes the manifest's `size`, and invalid gzip stops it too: `Storage.CorruptObject`, that path skipped (base kept, exit `9`), the rest of the sync continues; a truncated object fails the hash.
   - Before overwriting or deleting a local file, **re-check it still matches what the scan saw** (size + mtime). If it changed meanwhile, skip that path; it gets picked up next run.
   - Local deletes remove the file, then remove parent directories that became empty (unless the manifest has them as `dir` entries).
9. **Update state:** new `base` = committed manifest, **except** paths whose local apply failed or was skipped keep their *old* base entry, so the next run sees that change again. Write `.mysynctool` atomically. Release the lock.

**Rules the pipeline adds on top of the merge** (all tested in `application-unit-tests/Sync`):

- **Paths the sync can't see are frozen.** If the scan skipped a path (unstable, unreadable, symlink) or the local ignore rules match it, its local side is taken to be its base, and its action is deferred. A skipped *directory* freezes everything under it too: an unreadable folder must not look like "all its files were deleted here". A file that is absent from the scan because it is busy or ignored is never read as "deleted here", and nothing is downloaded over it. The manifest keeps its remote entry; a remote-only ignored entry is reported as `IgnoredLocally`.
- **Deferred means "keep the old base".** An upload the store rejects (`HashMismatch`, the file changed mid-upload), a file changed after the scan, a failed apply, a case-only collision on a case-insensitive filesystem, or a Windows-invalid name on Windows: the path is left out of this run (`SyncPlan.WithDeferred`), keeps its base entry, and is reported in `SyncReport.Skipped` with a `SkipCause`. The next run sees it again.
- **Phase 8 deletes first**, deepest path first, then downloads, directory creations and mode changes in parallel, so a file can become a directory (and back) in one run. A directory that isn't empty when it should be deleted is deferred, not forced.
- **A conflict's remote details** (`remote.revision`, `updatedBy`) come from the HEAD the plan was made against, not from this run's own commit. An unchanged conflict keeps them and its `firstDetectedUtc`.
- **A transfer that fails mid-stream skips that path, never the run.** An `IOException` while downloading (the network dropped, the disk filled) or while reading a local file for upload (it got locked) becomes a skip (`ApplyFailed` / `Unreadable`) and the path is retried next run. Stores turn their SDK's mid-read failures into `IOException` with a message of their own.
- Upload progress counts an object the store already had as transferred, so the bar completes; `SyncReport.Uploaded` counts only files actually sent. Progress counts **uncompressed** bytes read from the file, so percentages match file sizes; `TransferTotals.StoredBytes` is what went over the wire (`↑ 12 files 1.3 GB (sent 142 MB)`).

This order makes a crash at any point safe to re-run. If it dies after the commit but before the downloads, the next run sees local == base for those paths and remote changed, so it just downloads them. If both sides changed a path to the same hash, that's a no-op.

### Merge decision table

`B` = base entry, `L` = local entry, `R` = remote entry. Compare by `(type, sha256, exec)` via `ManifestEntry.AreEquivalent` (`∅` = absent; mtime is never compared). An exec-only change on one side is an ordinary local- or remote-wins change; an exec change on one side and a content change on the other is a conflict (row 4), like any two different changes. Evaluate the rows **in order**:

| # | Condition | Action |
|---|---|---|
| 1 | `L == R` | No-op (unchanged, same change on both sides, or deleted on both). Set base to `L`. |
| 2 | `L == B`, `R != B` | **Remote wins.** `R = ∅` → delete local; otherwise download `R`. |
| 3 | `R == B`, `L != B` | **Local wins.** `L = ∅` → remove entry from manifest; otherwise upload `L`. |
| 4 | `L != B`, `R != B`, `L != R` | **Conflict: drop and log.** Nothing happens to that path on either side: no upload, no download, no delete, no rename. |

Row 4 covers every kind of conflict: modify/modify, add/add (no base, different content), local modified + remote deleted, local deleted + remote modified, and type conflicts (file on one side, directory or a file under it on the other).

`MergePlanner.Plan(base, local, remote, MergeOptions)` returns a `SyncPlan` with one `SyncAction` per path. Remote-wins kinds are `Download`, `UpdateLocalMode` (exec bit only), `CreateLocalDirectory` and `DeleteLocal`. Local-wins kinds are `Upload`, `UpdateRemoteMode`, `AddRemoteDirectory` and `RemoveRemote`. The rest are `NoChange`, `Conflict` and `Skip`. Each action also says what each side holds afterwards (`LocalResult`, `RemoteResult`) and its `NewBase`.

**Paths are decided one by one, then checked as trees.** Per-path decisions can combine into something no filesystem can hold: local adds a file `a` while remote adds `a/b`. Wherever the resulting manifest or the resulting local folder would have an entry inside a file, the actions that caused it become `type` conflicts. The planner repeats that until both results are consistent. Replacing a directory with a file (or the reverse) on one side only is not a conflict: the other side's entries are removed and the new one is added.

#### Conflict handling: drop and log

The tool **never resolves a conflict on its own** and never creates conflict copies. For a conflicted path:

- **Local:** the file is left exactly as it is.
- **Remote:** the committed manifest keeps the remote entry unchanged. The local change is **not** included.
- **Base:** the path keeps its *old* base entry. The conflict stays visible on every later sync until it is resolved.
- **Logged** in three places:
  1. The end-of-run summary (`SyncReportView` on stderr; under `conflicts` in `--json` output), with path, kind, and the local and remote hash, size and mtime.
  2. The `conflicts` list in `.mysynctool` (§7). `status`, `info` and `conflicts` read it from there.
  3. An append-only line in `.mysynctool.log` (§7). Written only when a conflict is **first detected or its hashes change**, not on every run, so the log doesn't fill with repeats.
- The run **continues** for all other paths and exits with code `3`.

Only the machine that syncs second sees the conflict. The first machine's change was already committed cleanly. That's intended: the person who has to decide is the one told about it.

A conflict clears itself on the next sync when either:

- the local file is made identical to the remote one (`L == R`, row 1), or
- the local file is put back to its base content (`L == B`, row 2), which downloads the remote version.

Otherwise it's resolved explicitly with `mysync resolve` (§10).

- `--no-delete`: turns every delete action (local or remote, including replacing a file with a directory) into a skip, and keeps the base entry so it is reconsidered next run. An action that can't happen because of such a skip (downloading `a/b` while the file `a` is kept) is skipped too, not reported as a conflict.

### Creating a share (`mysync sync` with no `.mysynctool`)

1. Refuse if the folder is the user's home directory, a drive or volume root, or `/`, unless `--force` (`ShareErrors.UnsafeRoot` → exit `4`).
2. `PreviewNewShareQuery`: file count and total size.
3. If stdin is interactive and the folder has > 1,000 files or > 1 GB, the **command** asks for `y/N` (not a credential prompt, so it's allowed). If stdin isn't interactive, `--yes` is required above those thresholds.
4. `CreateShareCommand`: generate `shareId`, create the container if needed, hash and upload objects, commit `manifest.json` (revision 1) with `If-None-Match: *`.
5. Write `.mysynctool` with base = that manifest. Print the share ID and the exact command to clone it.

### Cloning a share (`mysync sync <shareId>`, `CloneShareCommand`)

1. Validate `<shareId>` is a UUID (validator). A `.mysynctool` in the folder → `ShareErrors.AlreadyAShare` → exit `4`: "already a share; run `mysync sync` without an ID". If it's a different share, say so.
2. **The folder must be empty.** Ignore only the built-in OS junk (`.DS_Store`, `Thumbs.db`, `desktop.ini`). Anything else → `ShareErrors.FolderNotEmpty` → exit `4`. `--force` allows a non-empty folder. Local files are then treated as `L` against an empty base: files that exist on both sides with different content become conflicts (row 4, dropped and logged), and local-only files are uploaded (row 3). Nothing is silently overwritten.
3. Read the manifest (not found → `ShareErrors.NotFound` → exit `8`). Download everything (parallel, verified), apply exec bits and mtimes.
4. Write `.mysynctool` with base = that manifest.

### `mysync clone <share> [<folder>]` (`CloneIntoFolderCommand`)

Like `git clone`: the folder is picked or created, then **the clone above runs unchanged** in it. `mysync sync <share>` in an empty folder works exactly as before; `clone` is an addition. There is no `--force`: cloning into a folder that has files stays `mysync sync <share> --force` inside it.

- **The share first, before the disk is touched** (`ShareResolver`, then HEAD): an unknown name, a deleted or unfinished share, rejected credentials, "upgrade mysync" fail with their usual errors and exit codes, and no folder is created.
- **The folder** (`CloneTarget`, Domain, pure): `<folder>` if given (relative to the current folder); otherwise a new folder named after the share inside the current one: the typed name, or for an ID its first name in ordinal order (`IAliasStore.FindByShareAsync`; several after an interrupted rename). An ID with no name → `Clone.FolderRequired` (exit 2).
- **The target** (`ICloneFolders.Inspect`): missing → created with any missing parents (`Create` returns which, outermost first); an existing folder holding only OS clutter (or mysync's own artifacts) → cloned into; with files → `Shares.FolderNotEmpty` (exit 4, unchanged); a file → `Clone.TargetIsAFile` (4); the target or a parent has a `.mysynctool` (`ILocalSources.FindSyncedFoldersAsync` on the nearest existing folder) → `Clone.InsideAShare` (4); the home folder, a drive or volume root, `/` → `Shares.UnsafeRoot` (4); can't be created → `Clone.CannotCreate` (4), with nothing left of the attempt.
- **Running it there** (`ICloneRunner`, the CLI's `FolderCloneRunner`): the composition root's own `IServiceCollection` is built again with `ShareRoot` replaced by the target, so every folder-bound service (folder, scanner, state, lock, ignore rules, conflict log) is rooted there; the host's `ILoggerFactory` is reused (a second Serilog factory could dispose the shared sinks). **The current directory never changes.** The clone gets the share **by ID**, so a name re-pointed meanwhile can't switch shares.
- **After a failure, an exception or Ctrl+C** (`CloneCleanUp`, then rethrow or return the clone's own error): what the clone wrote goes, and nothing else. A file is the clone's if it sits at a path of the share (HEAD before the clone, or HEAD now) with that entry's size and mtime (±2 s; the clone sets the manifest's mtime). Then the share's folders that are now empty, deepest first; mysync's artifacts that weren't there before; then the folders the command created, innermost first, each only if empty (OS clutter counts as empty, as in §8). A target that existed is kept, emptied of what the clone wrote. Anything else (a file written meanwhile, one with other content) stays, with its folders, and is reported (`CloneCleanUpReport.LeftBehind`, through the command's `CleanedUp` callback, since a cancellation can't return it): after the original error on stderr, and as `Clone.LeftBehind` details in `--json`.
- **Skipped paths or warnings are a success** (exit 9 / 0): nothing is removed.
- **Output:** the clone summary with `into ./<folder>` (relative inside the current folder, else in full), then `Next:  cd <folder>` unless it is the current folder, quoted by `ShellQuote` (`CloneLocation`). `--json`: the clone report plus `folder` (full) and `createdFolder`.

---

## 10. CLI surface

```text
mysync sync --name <name> [--compress] [--dry-run] [--yes] [--force]   # in a folder that isn't a share: create one (name required)
mysync sync [<name|shareId>] [--dry-run] [--no-delete] [--allow-mass-delete] [--yes] [--force]
mysync clone <name|shareId> [<folder>]   # clone into a new folder named after the share (or <folder>), created for it (§9)
mysync shares            # every named share: name and full share ID (the alias listing only)
mysync rename <name>     # in a share's folder: claim a new name, free the old one(s)
mysync compression [on|off] [--share <name|id>]   # show, or turn on/off for files uploaded from now on (§6)
mysync status            # read-only: the plan sync would execute, plus conflicts/warnings
mysync info [<name|shareId>]   # names, share id, account/container, base revision vs remote revision, file count, size, open conflicts
mysync conflicts         # list open conflicts with local vs remote hash/size/mtime/who
mysync diff [<path>...] [--base] [-U|--context <n>] [--stat] [--print local|remote|base]   # what differs (read-only)
mysync resolve <path>... --keep local|remote [--dry-run]
mysync resolve --all     --keep local|remote [--dry-run]
mysync gc [--dry-run] [--keep-revisions 20] [--grace-hours 24]
mysync delete [<name|shareId>] --yes   # delete a share for every machine and free its names; local files stay
mysync update [--check] [--version <v>] [--prerelease]   # replace this binary with a verified release
mysync push <name|shareId> [<path>...] [--updated-since <duration|time|last>] [--include <pattern>]... [--exclude <pattern>]... [--max-size <size>]
            [--host <name>] [--create [--compress]] [--no-info] [-n|--dry-run] [--force]   # add-only upload into <host>/<UTC time>/ (§10b)
mysync pull <name|shareId> [<share-path>...] [--to <folder>] [--latest] [--host <name>]... [--overwrite] [-n|--dry-run]   # read-only download into an ordinary folder (§10c)
mysync publish [--share <name|id>] [--revision <n>] [--title <text>]   # public, read-only copy of a snapshot (§10a)
mysync publications [--share <name|id>]                   # the share's publications
mysync unpublish <publishId> [--share <name|id>]          # take one down (its link dies first)
mysync --version
mysync tui [--name <name>] [--compress] [--ascii] [--no-color]   # the terminal interface for this folder (§10d)
mysync self-check       # runtime checks inside the shipped binary (Unicode normalization); exit 1 if any fail
```

Global options: `--verbose`, `--quiet`, `--json` (machine-readable result on stdout; logs go to stderr), `--parallel <n>`.

### `resolve` (the only way a conflict is decided)

Runs the normal pipeline (lock → scan → read remote → plan), but for the named conflicted paths it overrides row 4:

- `--keep local`: upload the local version (or record the local delete) and commit it with `If-Match`. On precondition failure, re-plan. If the path's remote side changed again in the meantime, it's still a conflict: report it and don't force it.
- `--keep remote`: download the remote version over the local file (or delete the local file if the remote side deleted it). Re-check the local file still matches the scanned hash before overwriting, as in phase 8.
- Sets the base entry for the path to the resolved state, removes it from `conflicts`, and writes a `RESOLVED` line to `.mysynctool.log`.
- A path that isn't currently in conflict → error for that path, exit `2`. `resolve` touches only the named conflicted paths. It doesn't run a full sync of other paths.

How it's built (`SyncSession.Resolve`, `SyncPipeline.PlanAgainst`): for each named path the planner's **base is moved to the side being given up**, the remote entry for `--keep local` and the local one for `--keep remote`. Then the ordinary rows carry the kept side across: row 3 uploads or removes, row 2 downloads or deletes. Every other path is deferred. A named path whose remote no longer matches the remote recorded in its conflict (or that the scan couldn't see) keeps its real base, so it stays a conflict. Every other open conflict is carried over exactly as recorded and isn't logged again. The report lists only the named paths: `Resolved`, and in `Conflicts` the ones still open, so an unrelated open conflict doesn't make a successful `resolve` exit `3`. Mass-delete isn't guarded: the user named every path.

`--keep` accepts `local` / `remote` in any case. The CLI moves `resolve`'s options in front of its paths before parsing (`KnownCommands.ArrangeForParsing`), because ConsoleAppFramework's `params` argument would otherwise swallow `--keep local`. On Windows `\` in a typed path becomes `/`; a leading `./` is dropped.

### `status`, `info`, `conflicts`

- `status` (`SyncPipeline.PreviewAsync`): scan, read HEAD, plan, exactly as a sync would, and **write nothing**: no lock (it runs during a sync), no staging clean-up, no state (not even the hash cache), no log. A mass-delete that would stop a sync is a warning. Exit codes as for a sync: `3` with conflicts, `9` with skipped paths.
- `info`: state plus HEAD, so it needs storage. Shows base revision against HEAD revision, who changed HEAD last, file count and size, open conflicts.
- `conflicts`: `.mysynctool` alone, offline, no lock. Exit `0` whether or not any are open; scripts read `--json`.
- All of them, like `sync` and `gc`, fail outside a share with `ShareErrors.NotAShare` → exit `4`.

### `diff` (seeing what differs before `resolve`)

`DiffQuery` / `PrintVersionQuery` (`Application/Conflicts/Diff`), engine in `Domain/Diffing` (Myers line diff, `UnifiedDiff`, `TextContent`, one `DiffLimits`). A **query**: no lock, no state (not even the hash cache), no staging, no `.mysynctool.log`, nothing written into the share folder (there is no `--write`).

- **No paths:** every open conflict, in path order. **A conflicted path** is compared with the remote version **recorded in the conflict** (`Conflict.Remote`, what `--keep remote` applies), noting when HEAD has moved on; **any other path** with HEAD (read once per query). The local side is always the file on disk now, noting when it no longer matches the conflict's local hash. Neither local nor in HEAD → `Diff.PathNotFound` (exit 2).
- **By kind:** modify/modify and add/add local → remote (`--base`: base → local "your changes" and base → remote "their changes"); modify/delete base → local plus "the share deleted this file"; delete/modify base → remote plus "you deleted this file"; `type` a description only.
- **Outcomes:** identical, text (hunks), binary (NUL in the first 8 KiB, or not valid UTF-8/UTF-16-with-BOM), too-large (> 8 MiB or 200,000 lines a side; never buffered: at most 8 MiB + 1 byte is read into memory, a larger stored object isn't downloaded), eol-only (lines equal once `\r` is ignored), exec-only, type-mismatch. Lines split on `\n`, `\r` kept for comparison and the patch, hidden on screen. An encoding or BOM difference is a note.
- **Stored versions are hashed before use** (`DiffSources`); a missing object is `Diff.VersionNotStored` (exit 8, e.g. after `gc`), a mismatch `Storage.HashMismatch`. The local executable bit comes from the recorded conflict side (or base) when the content matches, since `ILocalFolder` doesn't report it.
- **Output** (`Rendering/DiffView`): on a terminal that can redraw, colours with file content escaped (`Markup.Escape`) and control characters shown (`TerminalText.Visible`, ESC → `␛`); otherwise plain UTF-8 in exactly the patch form (labels `local/`, `remote/`, `base/`, `/dev/null` for an absent side), so `mysync diff x > x.patch` applies with `git apply`. Conflicts end with `resolve` commands for that kind (`DiffView.Labels`), paths quoted by `ShellQuote` (POSIX `'\''`, PowerShell `''`, bare when only `[A-Za-z0-9._/-]`).
- **`--print`:** one path; not with `--stat`, `--base` or `--json` (`Diff.PrintNeedsOnePath` / `PrintCombined`, exit 2); raw bytes through `Console.OpenStandardOutput()`; unlimited, a stored version's hash checked as the stream ends. `--print local` needs no storage.
- Options go before or after the paths (`KnownCommands.ArrangeForParsing`, as for `resolve`). Exit `0` whether or not anything differs.
- `conflicts` and every conflict summary point at `mysync diff`; `conflicts` lists a `mysync diff <path>` per conflict.

### `delete` (removing a share, e.g. sensitive data after a work session)

`DeleteShareCommand`, in this order so every machine learns at once and an interruption is safe to re-run:

0. **Publications first** (§10a): each is taken down by `PublicationRemover` (`index.html` first), while its record still exists. If the share has publications and the publishing variables aren't set, `delete` refuses before changing anything (`Publish.PublicationsNeedSettings` → exit `2`): the public copies would otherwise outlive the only record that finds them. A share without publications needs no publishing variables.
1. **The marker first.** HEAD is replaced, conditionally (`If-Match`, retried with back-off like a commit), by `manifest.CreateDeletedRevision(now, machine)`: the next revision, format 2, no entries. Any sync in progress elsewhere then fails its commit, re-reads and stops.
2. **Then the sweep:** every object, then the history copies of every revision up to the marker (missing = success; history lists file paths, which can be sensitive), then `publications.json`. The marker itself stays as HEAD for good.
3. **Recoverable copies:** `IShareStore.CountRecoverableCopiesAsync` (Azure: soft-deleted blobs and old versions under the share's prefix). A non-zero count is a warning: the data can be restored until the account's retention ends.
3a. **Its names are freed**: every alias pointing at the share (`ShareNames.FreeAsync`: find by share ID, read each again, delete with `If-Match`, so a name re-pointed meanwhile is never removed). Same whether `delete` was given a name or an ID, and also when a re-run finds the marker already written. A name whose share was **never finished** (alias, no manifest: an interrupted create) is deleted without a marker: its objects, history and records are swept, then the name (`DeleteShareResponse.WasIncomplete`). By ID, a share with no manifest and no name is still `NotFound`.
4. **This folder**, if it is that share: `.mysynctool`, `.mysynctool.log` and the staging directory are removed (`IShareStateStore.DeleteAsync`, `IConflictLog.DeleteAsync`); **files are kept**, and the folder is an ordinary folder again (a new `sync` creates a new share). With a share ID from another folder, that folder isn't touched.

- Already marked deleted → it sweeps again and succeeds (`WasAlreadyDeleted`). So re-running finishes an interrupted delete, and on another machine `mysync delete --yes` in the deleted share's folder just drops that folder's state.
- **Every other command** that reads HEAD (`sync`, clone, `status`, `info`, `resolve`, `gc`) fails with `ShareErrors.Deleted` (who and when) → exit `8`, and changes nothing: other machines' files are **never** deleted because of it. `conflicts` (offline) still lists what `.mysynctool` recorded.
- A sync that uploaded objects and then finds the marker at commit time deletes the objects *it* stored (`UploadOutcome.Sent`), so nothing lingers after the sweep.
- **No prompt:** `--yes` is required (`Cli.DeleteNeedsYes` → exit `2`), so the large-share `y/N` stays mysync's only prompt.

### `update` (self-update; never automatic)

mysync contacts the release source **only** when `update` runs: no background checks, no "new version" hints (the tool's promise is no backend but storage, and CI/offline runs stay predictable). `UpdateCommand`:

1. **Pick the release:** the newest (prereleases only with `--prerelease`), or exactly `--version <v>` (also an older one, so a team can pin one version). "Latest" never goes backwards. `--check` stops here and downloads nothing.
2. **Check the binary can be replaced** before downloading anything: a development build (`dotnet mysync.dll`) can't (`Update.NotInstalledBinary` → exit `4`), nor a folder this user can't write (`Update.NotWritable` → exit `4`, with advice: `sudo`, or install into `~/.local/bin`).
3. **Signed checksums first:** download `SHA256SUMS.txt` and `SHA256SUMS.txt.sig`; the signature (ECDSA P-256, SHA-256, DER) must verify against the **trusted key** (`UpdateOptions`), or nothing large is even downloaded. An unsigned release (0.3.0 and older) is refused (`Update.SignatureMissing`).
4. **The archive**, hashed while written into a workspace **next to the executable** (same volume): it must match the signed checksum.
5. **Extract only the executable** (to a path of our choosing, never the entry's), run it: `--version` must print the release and `self-check` must pass (`Update.NewBinaryFailed`).
6. **Swap** (`IUpdateWorkspace.InstallAsync`): Unix renames over the old file (the running process keeps its copy). Windows can't overwrite a running .exe, so it renames it aside (`mysync.exe.old-<guid>`), moves the new one in (moving the old back if that fails), and `UpdateLeftovers.RemoveNextTo` deletes the old copy at a later start, along with workspaces an interrupted update left (> 1 h old).

Until step 6 the installed binary is untouched. The platform to fetch is the one the binary was built for (`RuntimeInformation.RuntimeIdentifier`). A symlinked install is resolved and the real file replaced.

- **Release source** (`ReleaseSourceSelector`, `MYSYNC_UPDATE_SOURCE`): unset → the GitHub repository the binary was built with (`-p:MySyncReleaseRepository`, below), or `ReleaseAssets.DefaultRepository` for a local build; `github:owner/repo` → that repo; anything else → a local folder (`DirectoryReleaseSource`) laid out like GitHub releases (`<root>/mysync-1.2.3/<assets>`), for a mirror, an offline machine, or tests. GitHub lists `releases?per_page=100` (only `mysync-*` tags, never drafts) and downloads through the API asset URL; `GITHUB_TOKEN` (optional) is sent only to api.github.com (.NET drops it on the redirect) and never echoed. A rate limit or a private repo without a token gets a message that says so.
- **The trusted key and repository are fixed at build time** (`-p:MySyncReleasePublicKey=<base64 SPKI>`, `-p:MySyncReleaseRepository=owner/repo` → assembly metadata). The release workflow sets both from the repository it runs in (§16); a local build falls back to `ReleaseSignature.ProductionPublicKeyPem` and `ReleaseAssets.DefaultRepository`. No environment variable can make a binary trust another key.
- `ReleaseVersion` orders by Semantic Versioning 2.0 (prereleases before their release). `IsSignificantChangeFrom` (major or minor changed) makes the CLI tell people to read the notes and update every machine together (e.g. the delete marker in 0.3).
- `ReleaseAssets` is the one place release names are built (tag, archive per RID, `SHA256SUMS.txt`, `.sig`); `ReleaseAssetsTests` pins them and checks the release workflow publishes every RID and the signature.

### `gc` (never automatic)

Deletes object blobs in the share that are **not referenced** by HEAD or by any of the last `--keep-revisions` history manifests, **and** whose `LastModified` is older than `--grace-hours`. The grace period protects objects uploaded by another client's sync that hasn't committed yet. Prune history manifests older than the kept window. Always print what will be (or was) deleted.

- `--keep-revisions` ≥ 1 and `--grace-hours` ≥ 1 (validator; a zero grace could delete what another machine is about to commit).
- Anything unreadable (a history manifest, the object listing) stops gc **before** it deletes anything: it never guesses what is referenced.
- HEAD is read again just before deleting; if it moved, nothing is deleted (`SyncErrors.ShareChangedDuringGc` → exit `5`). Objects are deleted first, then history.
- **Known risk:** a sync that starts *after* that check and finds an old, unreferenced object still present reuses it instead of uploading, and gc may delete it a moment later. Run gc when no one else is syncing. Objects an interrupted *create* uploaded sit under a share ID with no manifest; gc (per share) doesn't see them.

### Exit codes (`apps/mysync/ExitCodes.cs`)

Mapped from `Error.Code` (failures) or from the report (partial successes). Nothing below `apps/` knows about exit codes.

| Code | Meaning | Source |
|---|---|---|
| 0 | Success, nothing notable | |
| 1 | Unexpected error | unhandled exception |
| 2 | Usage or configuration error | `ValidationError`, `ConfigurationErrors.*` (including `WrongRegion`, `BucketNotFound`, no AWS credentials), `StorageErrors.ClockSkew`, `PublishErrors.NotPublic`, `PublicListing`, `resolve` on a non-conflicted path; `ShareErrors.NameRequired`, `InvalidName`, `InvalidReference`; `Cli.NameWithShareReference`, `Cli.RenameNeedsName`; `DiffErrors.PathNotFound`, `VersionMissing`, `PrintNeedsOnePath`, `PrintCombined`, `Diff.InvalidContext`, `Diff.InvalidPrint`; `Cli.CompressionNeedsOnOrOff`; `PublishErrors.StaticWebsiteDisabled`, `IndexDocumentMismatch`, `OtherAccount`, `PublicationsNeedSettings`; `PushErrors.DuplicateSource`, `SourceNotFound`, `InvalidTarget`, `CreateNeedsName`, `InvalidSince`, `InvalidPattern`, `InvalidSize`, `Cli.PushNeedsShare`, `PullErrors.PathNotFound`, `DuplicateTarget`, `HostNotFound`, `LatestWithPaths`, `HostNeedsLatest`, `Cli.PullNeedsShare`, `TuiErrors.NotATerminal`, `TooSmall`; `CloneErrors.FolderRequired`, `Cli.CloneNeedsShare` |
| 3 | Completed, but one or more conflicts were dropped and logged (still open) | `SyncReport.Conflicts` non-empty (takes precedence over 9) |
| 4 | Folder precondition failed, or the name is taken | `ShareErrors.FolderNotEmpty`, `AlreadyAShare`, `NotAShare`, `UnsafeRoot`, `NameTaken`; `UpdateErrors.NotWritable`, `NotInstalledBinary`; `PushErrors.SourceIsTheShare`, `TargetIsAFile`; `PullErrors.DestinationOccupied`, `DestinationIsTheShare`; `CloneErrors.TargetIsAFile`, `InsideAShare`, `CannotCreate` (and `clone`'s own `Shares.FolderNotEmpty` / `UnsafeRoot` wording) |
| 5 | Remote concurrency retries exhausted | `SyncErrors.CommitRetriesExhausted` (push: after 20 attempts), `SyncErrors.ShareChangedDuringGc`, `PublishErrors.RegistryRetriesExhausted` |
| 6 | Another mysync process holds the lock for this folder | `StateErrors.FolderLocked`; `TuiErrors.AlreadyRunning` (another `mysync tui` here) |
| 7 | Safety guard tripped (mass delete) | `SyncErrors.MassDeleteBlocked` |
| 8 | Share not found, deleted, unfinished or no such name, or storage auth failure; no such release or publication | `ShareErrors.NotFound`, `ShareErrors.Deleted`, `NameNotFound`, `Incomplete`, `DiffErrors.VersionNotStored`, `StorageErrors.UnsupportedEncoding`, `StorageErrors.Unauthorized` (also an expired AWS session), `UpdateErrors.ReleaseNotFound` / `NoBuildForPlatform`, `PublishErrors.NotFound` / `RevisionNotFound` / `Unauthorized` / `CannotReadServiceProperties`, `PullErrors.NoPushes` |
| 9 | Completed, but some paths were skipped (locked, unstable, invalid name) | `SyncReport.Skipped`, `PushReport.Skipped` or `PullReport.Skipped` non-empty (push never exits 3 or 7; pull never 3, 5 or 7) |
| 10 | Published, but the publication couldn't be opened anonymously (check `MYSYNC_PUBLISH_BASE_URL`) | `PublishReport.Verified` false |
| 130 | Cancelled (Ctrl+C) | `OperationCanceledException` from the caller's token; `mysync tui` ended by a second Ctrl+C (or a stop) during a job |

### Output

- Default: concise progress (`↑ 12 files 4.1 MB  ↓ 3 files 220 KB  ✗ 0`) and a final summary listing conflicts and skipped paths with reasons. The summary goes to stdout; progress, warnings and failures go to stderr.
- `--json` documents (`Rendering/JsonOutput`, source-generated `CliJsonContext`) all end with `exitCode`. `sync` / `clone`: `command`, `shareId`, `revision`, `committed`, `dryRun`, `uploaded` `{files, bytes, storedBytes}` (`storedBytes`: what was sent, less than `bytes` when compressed) / `downloaded` `{files, bytes}`, `deletedLocally`, `deletedRemotely`, `conflicts` (full entries, like `.mysynctool`), `skipped` `{path, cause, detail}` (cause in kebab-case, e.g. `changed-since-scan`), `warnings`, and with `--dry-run` a `plan` of `{path, action}`; `mysync clone` adds `folder` (full path) and `createdFolder`, and a failed one lists what its clean-up left as `Clone.LeftBehind` `details`. Create: `shareId`, `name`, `compression` (`gzip` | `none`), `fileCount`, `totalBytes`, `uploaded`, `skipped`, `warnings`, `cloneCommand` (`mysync clone <name>`). `shares`: `shares` (`name`, `shareId`, `createdUtc`, `createdBy`), `warnings`. `diff`: `shareId`, `items` (`path`, `kind` or `null`, `local` / `remote` (+ `revision`, `updatedBy`) / `base` as `{type, sha256, size, mtimeUtc, exec}` or `null`, `sections` (`title`, `oldLabel`, `newLabel`, `outcome` identical|text|binary|too-large|eol-only|exec-only|type-mismatch, `added`, `removed`, `hunks` (`oldStart`, `oldLines`, `newStart`, `newLines`, `lines` `{op, text}`, `noNewlineAtEnd {old, new}`; omitted with `--stat`), `oldEol`/`newEol`, `oldExec`/`newExec`, `oldSize`/`newSize`, `description` as they apply), `notes`, `resolve` `{keepLocal, keepRemote}` or `null`). `rename`: `shareId`, `name`, `previous`, `unchanged`. `compression`: `shareId`, `name`, `compression`, `revision`, `formatVersion`, `changed`. `info`: also `names`, `compression`; `rootPath`, `baseRevision`, `openConflicts` are `null` when this folder isn't the share. `delete`: also `namesFreed`, `wasIncomplete` (then `markerRevision`, `deletedUtc`, `deletedBy` are `null`). `publish`: `shareId`, `publishId`, `url`, `revision`, `title`, `files`, `bytes`, `copies` (`{put-blob-from-url|blocks-from-url|streamed: n}`), `verified`, `verificationDetail`, `shellCommand`, `powerShellCommand`, `skipped`, `warnings`. `publications`: `shareId`, `publications` (`id`, `status` pending|published|failed, `title`, `revision`, `files`, `bytes`, `createdUtc`, `createdBy`, `publishAccount`, `url`, `siteAssetsVersion`). `unpublish`: `publishId`, `url`, `blobsDeleted`. `delete` also has `publicationsRemoved`. `push`: `shareId` (`null` only for a dry run of `--create`), `shareName`, `target`, `revision`, `committed`, `dryRun`, `created`, `files`, `bytes`, `uploaded` `{files, bytes, storedBytes}`, `alreadyStored` `{files, bytes}`, `commitAttempts`, `skipped`, `warnings`, `ignoredByShare`, `compression` (`gzip` | `none`: the share's setting, which this push's uploads followed), `filters` (`null` without filter options, else `updatedSince` (resolved cutoff, UTC to the second, or `null`), `updatedSinceSpec`, `include`, `exclude`, `maxSizeBytes`), `filteredOut` `{total, notChanged, notIncluded, excluded, tooLarge}` (all zeros without filters). `pull`: `shareId`, `shareName`, `revision` (the one HEAD everything came from), `destination` (full path), `dryRun`, `latest`, `selected` (`{sharePath, localPath}`; empty strings: the whole share, the destination root), `files`, `bytes` (downloaded + already there), `downloaded` / `alreadyThere` `{files, bytes}`, `overwritten`, `skipped`, `warnings`. A failure: `{ "error": { code, description, details? }, exitCode }`.
- When the terminal can't redraw, no animated progress (see §4).
- Messages say what happened and what to do next. No stack traces unless `--verbose`.

---

## 10a. Publishing (`mysync publish`)

`publish` turns a snapshot of a share into a **public, read-only, self-contained static website**: anyone with the link can browse and download, with no server and no auth. It's hosted by the **Azure Storage static website** feature (`$web`) of the publishing account (`MYSYNC_PUBLISH_CONNECTION_STRING`). Files are copied **server-side** from the share's content-addressed objects, so a publication is independent of the share: later syncs, `gc` and `resolve` never change it. It stays until `unpublish`, or until `delete` removes the share.

### Public layout (`PublicationLayout`, the one place these names are built)

```text
$web/
├── index.html, 404.html, robots.txt   # the root: CLI-owned (libs/mysync/site-assets/Root), uploaded If-None-Match: *
└── p/<publishId>/
    ├── index.html                     # from the mysync-site build; uploaded LAST: the commit point
    ├── assets/app.js, app.css, THIRD-PARTY-NOTICES.txt
    ├── site-data.js                   # window.__MYSYNC_SITE__ = {...};
    ├── SHA256SUMS.txt, download.sh (LF, POSIX sh), download.ps1
    └── files/<the file's path in the share>
```

- `publishId`: 128 bits from `RandomNumberGenerator`, base62, 22 characters (`PublishId`). **Never derived from the share ID.** Nothing public names the share, and with a separate publishing account nothing names the private account either. The same account is allowed (the product owner's call); `publish` then warns that the URL names it.
- A share path that can't be a blob name (ending in `.`; longer than 1024 characters as a blob name) is left out and reported (`PublishReport.Skipped`).

### Data contract

`apps/mysync-site/contract/site-data.schema.json` (JSON Schema 2020-12) is the single source of truth for `window.__MYSYNC_SITE__`. `SiteDataBuilder` (Domain) builds it from a manifest: entries in ordinal path order, `dir` entries only for empty directories, totals, and each file's `disposition`. `SiteDataSerializer` (Infrastructure, source-generated) writes it compact with the default escaping encoder, so no name can end the string or the script (`<`, `>`, `&`, quotes and all non-ASCII become `\uXXXX`). `SiteDataContractTests` validates the output against the schema; the site validates its fixture against the same file.

### Headers and the disposition policy

- **Compressed objects stay compressed** on the way to `$web`: a server-side copy carries the source object's encoding to the published blob (`PublishedBlob.ContentEncoding`, `Content-Encoding: gzip`), learned from one listing of the share's objects (no download). Browsers, `curl --compressed` (`download.sh`) and PowerShell (`download.ps1`, which also gunzips itself if a PowerShell didn't: only a file that still starts like gzip and fails its hash, so a real `.gz` is never touched) all end up with the original bytes, and both scripts verify the SHA-256 of the **decoded** file. `site-data.js` sizes and hashes are uncompressed. The streamed fallback (no SAS) decodes through `ObjectCodec` and publishes **raw**. Checked against a real static website by the Azure storage tests in CI (`ARealStaticWebsite_Should_ServeGzipFiles…`; first on 2026-11-22: served with the header, decoded by every client); on S3 the public bucket serves the header too (`APublishedFile_Should_BeServedAnonymously…`).
- `Content-Type` from `ContentTypeMap` (by extension; default `application/octet-stream`). The page's own files: `text/html|javascript|css|plain; charset=utf-8` (a module script with the wrong type doesn't run).
- `Content-Disposition` from `DispositionPolicy`: `attachment; filename*=UTF-8''<RFC 8187-encoded name>`, except **inline** for `.txt .md .log .csv .json .pdf .png .jpg .jpeg .gif .webp`. **Never** inline `.html .htm .svg .xml .xhtml .js`: they would run script on the publishing origin, next to every other publication. The same answer goes into `site-data.js`, so the page opens inline files in a new tab and downloads the rest.
- `Cache-Control`: `public, max-age=31536000, immutable` for published files and the page's assets; `no-cache` for `index.html`, `site-data.js`, the checksums and scripts, so `unpublish` takes effect.

### `publish` (`PublishShareCommand`)

Preflight, before anything is written: the share (this folder's, or `--share <id>`); the publishing settings and an absolute `https` `MYSYNC_PUBLISH_BASE_URL` (`ConfigurationErrors` → `2`); same account → warning (`IPublishTarget.SharesAccountWith`: same provider and the same account, or the same bucket on S3); **`IPublishTarget.PreflightAsync`**, the target's own readiness check, of which the application knows only "ready or not" (Azure: static website on with `index.html`, `PublishErrors.StaticWebsiteDisabled` / `IndexDocumentMismatch`, with the exact `az storage blob service-properties update … --static-website --index-document index.html --404-document 404.html`; a credential that can't read service properties says so; mysync never turns it on. S3: see "Publishing on S3" below); not the Debug placeholder (`Publish.SiteAssetsMissing`); HEAD (or `--revision n` from history) loads and isn't the deleted marker. Publishing this folder's share at HEAD runs the read-only preview (`status`) and warns if there are unsynced changes: the publication is the last **synced** state.

Then, in order:
1. A `pending` record in `publications.json` (`PublicationRegistryUpdater`: ETag read-modify-write, the manifest's retry rules). Whatever follows can always be found and removed.
2. The `$web` root files, `If-None-Match: *`.
3. Every file, in parallel (`--parallel` / `MYSYNC_PARALLELISM`), **server-side** where the target can: the share's store offers a `CopySource` per object (`IShareStore.GetCopySourceAsync`: a short-lived readable URL, a native object reference, or both), and `IPublishTarget.CanCopy` says whether the target can use it. Azure copies from any readable URL (an Azure SAS, or an S3 presigned GET) with **Put Blob From URL** up to 5000 MiB, else **Put Block From URL** blocks (256 MiB) + commit, both setting the headers (`AzureCopyLimits`, checked 2026-11-18); S3 copies only an S3 object signed with its own credentials (`NativeObjectReference.CredentialScope`): **CopyObject** with `MetadataDirective=REPLACE` up to 5 GiB, a **multipart copy** (UploadPartCopy) above, aborted on failure. Otherwise (a credential that can't sign, an Azure share published to S3, another publishing profile) the object is streamed through this machine, decoded and its hash checked on the way (mismatch → `Storage.CorruptObject`, the blob deleted), published raw, and the output says so. The first failure stops the other copies.
4. The page's assets, then `site-data.js`, `SHA256SUMS.txt`, `download.sh`, `download.ps1` (`DownloadScripts`: `sh` single-quotes every path; `ps1` carries paths as base64 UTF-8, ASCII only, because PowerShell also takes curly quotes as string delimiters).
5. **`p/<publishId>/index.html` last** (`If-None-Match: *`): the commit point.
6. The record becomes `published` (URL, revision, files, bytes, created, by, publishing account, `siteAssetsVersion`).
7. **Verification:** `HttpPublicationVerifier` GETs `<folder>/site-data.js` anonymously (no credentials, three tries) and looks for the publication ID; a status in the target's `PublishLocation.NotServedStatusCodes` is reported as "not served" (Azure: 404; S3: 403 and 404, since S3 answers 403 for a missing key to anyone who can't list the bucket). Failure leaves the publication up, sets `PublishReport.Verified = false`, and the CLI exits `10`, pointing at `MYSYNC_PUBLISH_BASE_URL`.

**Links:** `PublicationLayout.FolderUrl` is `<base>/p/<id>/`, where the download scripts and `site-data.js` are; `PublicationLayout.Url(base, servesIndexDocuments)` is the link people open: the folder where the host serves index documents (`PublishLocation.ServesIndexDocuments`, Azure), else `<folder>index.html` (S3's REST address). The link is what's printed, in `--json` `url` and in the registry record; the one-liners use the folder.

Failure or Ctrl+C before step 5: best-effort delete of `p/<publishId>/` and the record marked `failed` (`unpublish` removes whatever cleanup couldn't).

### Publishing on S3 (`S3PublishTarget`)

A dedicated public bucket (`MYSYNC_PUBLISH_S3_BUCKET`), served over **HTTPS** through its REST address `https://<bucket>.s3.<region>.amazonaws.com` (bucket policy: anonymous `s3:GetObject` on `p/*` and `robots.txt` only) or CloudFront in front of it; the S3 website endpoint is http only and refused. **No dots in the bucket name** (they break TLS on the virtual-hosted address). `ServesIndexDocuments = false`; "not served" is 403 or 404. `PublishLocation.AccountName` is `s3://<bucket>`, so an Azure record can never match it.

`PreflightAsync`, in order, before anything is copied:
1. The bucket exists in the configured region (a one-key `ListObjectsV2`, which from another region gets a redirect naming the right one; else `Configuration.WrongRegion` / `BucketNotFound`). Not `GetBucketLocation`: signed for the wrong region, S3 answers it with access denied. This also resolves the credentials, whose scope `CanCopy` compares.
2. **The anonymous probe:** `p/.mysync-probe-<random>` (`text/plain`, a random token) uploaded, read back with a plain `HttpClient` through `MYSYNC_PUBLISH_BASE_URL` (200 with the token, three tries), deleted whatever happens. Otherwise `Publish.NotPublic` (exit 2), with the bucket policy to apply and the three things that block it (the bucket's Block Public Access, the account-level Block Public Access, an organisation policy), since mysync can't tell them apart.
3. **No public listing:** an anonymous `GET <base>/?list-type=2&prefix=p/` must not return a `ListBucketResult`; else `Publish.PublicListing` (exit 2). A listing would reveal every publication ID, the links' only secret.

Uploads are PutObject (or a multipart upload, through `S3Uploader`) with `Content-Type`, `Content-Disposition`, `Cache-Control` and `Content-Encoding`; root files `If-None-Match: *`. `DeletePrefixAsync` lists, deletes the publication's `index.html` first on its own, then the rest in `DeleteObjects` batches of 1,000, checking every per-key error of each 200 answer. Never an ACL, never a bucket setting.

### `publications`, `unpublish`, and `delete`

- `publications [--share <id>]`: the registry. Needs the private connection string only.
- `unpublish <publishId> [--share <id>]` (`PublicationRemover`): `index.html` **first** (the link dies at once), then the rest of `p/<publishId>/`, then the record. Refuses a publication recorded in another publishing account (`Publish.OtherAccount`). Re-running finishes an interrupted removal; it also removes a `failed` one.
- `delete` takes every publication down first (above, `delete` step 0) and refuses without the publishing settings when there are any.

---

## 10b. Collecting from servers (`mysync push`)

`push` is a **one-way, add-only upload** into **its own folder of an existing share**. It never downloads, never deletes, never raises a conflict, and leaves no state behind: it never reads or writes `.mysynctool`, never takes the folder lock, never creates the staging directory, and writes no conflict log. It works the same in a share folder and an ordinary one, and several pushes may run in one folder at once. Twenty servers can push to one share at the same moment; a laptop that cloned the share gets every pushed folder on its next `sync`.

```text
mysync push <name|shareId> [<path>...]
            [--updated-since <duration|time|last>] [--include <pattern>]... [--exclude <pattern>]... [--max-size <size>]
            [--host <name>] [--create [--compress]] [--no-info] [-n|--dry-run] [--force]
```

- **Where things land** (`PushSourceLayout`): no paths → the **contents** of the current folder directly under the target folder; a file → `<target>/<its name>`; a folder → `<target>/<its name>/…`. Paths may be absolute or relative, inside or outside the current folder. Two arguments with one name (NFC) → `Push.DuplicateSource`, a missing one → `Push.SourceNotFound` (both exit 2, before anything is read). A folder that is the home folder, a drive or volume root, or `/` is `Shares.UnsafeRoot` (exit 4) unless `--force`. A symlink argument is skipped like a symlink inside a folder.
- **The target folder** (`PushTarget`) is always `<host>/<UTC start yyyyMMdd-HHmmss'Z'>`: new on every run, so a push never meets existing files and **no conflict is possible** (a conflict needs a base and a local side that disagree with the remote; a push has neither). `<host>` is `IMachineIdentity.HostName` or `--host`, sanitised (`<>:"|?*/\`, control characters and unpaired surrogates → `-`, white space and trailing dots trimmed, empty → `host`, NFC); a reserved Windows name or anything else that fails `SharePath` / `PathRules.CheckWindowsCompatible` is `Push.InvalidTarget` (exit 2). If that exact folder is taken (anything at or under it, even an empty `dir`), `-2`, `-3`, … is appended, checked again on every commit attempt. A **file** named like the host segment at the share root is `Push.TargetIsAFile` (exit 4, "pass `--host`").
- **`PushPlanner`** adds the pushed entries under the first free folder to whatever HEAD is. It never replaces or removes an entry: every HEAD entry is in the result unchanged (the only exception: a `dir` entry of an empty host folder, which becomes implied). Property-tested over generated manifests.
- **A folder that already syncs with the target share is refused** (`Push.SourceIsTheShare`, exit 4, `--force` doesn't override it): the current folder, each folder argument and the folder of each file argument, and **each of their parents**, are checked for a `.mysynctool` (`ILocalSources.FindSyncedFoldersAsync`, only the share ID is read). Pushing it would add a second copy that the next sync downloads back. A damaged one there is `State.Corrupt`. A `.mysynctool` of **another** share is fine: never uploaded, never changed.
- **What is read** (`LocalSources.ListAsync`, the one place push enumerates files): everything, except mysync's own artifacts (`.mysynctool*`) and OS clutter, at any depth. No `.mysyncignore` (a pushed one is an ordinary file; under `<host>/<timestamp>/` it has no effect). Symlinks, unreadable folders, files that are locked or vanish, and names no `SharePath` can hold are **skipped** (reported, exit 9, the rest goes on). Names Windows can't hold and case-only collisions are pushed with a warning. Empty folders become `dir` entries; `mtimeUtc` and the executable bit are recorded as in a sync.
- **Filters** decide which listed files a push takes (`PushSelection`, applied to `ILocalSources.ListAsync`'s listing **before anything is opened**: path, size and mtime are all in it). Push stays stateless; nothing is compared file by file with earlier pushes. With no filter option, push is exactly as without them.
  - A file is pushed only if **every** filter given keeps it (`PushFilter.Decide`). One left out is counted once, under the **first** filter that left it out, in this order: `--updated-since` (`notChanged`), `--include` (`notIncluded`), `--exclude` (`excluded`), `--max-size` (`tooLarge`). Exclude therefore wins over include.
  - **Filtered is not skipped:** a filtered-out file isn't read or hashed, isn't in `Skipped` or the record's `skipped`, and never makes the run exit 9. Filters apply to files named as arguments too; leaving one out is a **warning** naming the file and the filter.
  - **With any filter option, empty folders aren't recorded**: folders exist only as far as pushed files need them.
  - Everything filtered out → `Nothing to push (203 files left out by filters).`, no commit, no folder, exit 0 (9 only with skips).
  - **`--updated-since`** (`PushSince`): files modified (listing mtime, UTC) **at or after** a cutoff. A duration back from the start (`30m`, `2h`, `1d`, `1w`: a whole number > 0 and `m|h|d|w`); an ISO 8601 time (`2026-11-23T04:00:00Z`; without an offset local time, `TimeProvider.LocalTimeZone`; a date alone is local midnight; a future time is refused); or **`last`**: the start time of this host's newest `<host>/<timestamp>[-n]` folder, read from the **folder name in HEAD** (`PushTarget.TryParseFolderName`, the one parser of that format), with `<host>` the same sanitised segment this push uses (so `--host` picks the history). No earlier push, or a share `--create` makes → no cutoff, everything passes (`no earlier push from web01; pushing everything`). Resolved once, from the HEAD read push already does; it doesn't move on a commit retry. A file modified during the previous push is pushed again by the next (identical content is stored once). Case-insensitive. Anything else → `Push.InvalidSince` (exit 2), listing the forms. A duration or time is resolved before storage is touched.
  - **`--include` / `--exclude`** (repeatable): gitignore patterns through the ordinary matcher (`IIgnoreRulesLoader.Compile`, all of one option's patterns as one rule set), matched against each file's path **relative to the target folder** (`push logs /var/log/app` → `app/server.log`), with its folders (`IsIgnoredOrInsideIgnored`), so `logs/` takes a whole folder. Case-sensitive on every OS. `PushPatterns.Check` refuses an empty pattern, a negation (`!`), a comment (`#`; `\#` for the name) and control characters (a line break would be a second rule): `Push.InvalidPattern` (exit 2). The CLI (`KnownCommands.ArrangeForParsing`) gathers every value of each into one JSON array, because ConsoleAppFramework neither collects a repeated option nor leaves `[ab]*.log` or `a,b` alone otherwise. No folder is skipped while listing: the filter is applied afterwards only.
  - **`--max-size`** (`ByteSize`): files **larger** than the limit (the listing's size; one that grows past it while read is still pushed) are left out. A positive whole or decimal number, optional unit `B`, `K`/`KB`/`KiB`, `M…`, `G…`, `T…` (binary), case-insensitive; else `Push.InvalidSize` (exit 2).
- **Growing files are captured, not skipped** (`PushFiles`): a file's length is taken when it is opened, exactly that many bytes are hashed, and later exactly that many are uploaded (`CapturedStream`) under that hash, so a log written meanwhile is stored as it was when opened (the entry has the captured size). If fewer bytes are left than captured, or the store refuses the upload as `Storage.HashMismatch` (the file was replaced or rewritten), it is captured again, up to **3** times, then skipped as `unstable`. The upload goes through `ObjectTransfers.UploadOneAsync` (exists check, compression per the share's setting, the store's own verification). No hash cache.
- **The share's own `.mysyncignore` and `.gitignore`**: each one HEAD has at the root is read (hash-checked, ≤ 1 MB) and compiled (`IIgnoreRulesLoader.Compile`), the two joined as a union as in a sync, and every pushed file, as it is named in the share, is matched (including its folders). Matches are still pushed, with a warning naming the first five and the files whose rules matched (`ignoredByShare` in `--json`): clones apply those rules to downloads. An unreadable one → a warning naming it; the other still applies.
- **Committing** is the §6 protocol through `RemoteCommitter`, re-planning on every attempt with `SyncOptions.MaxPushCommitAttempts = 20` (many servers at once), back-off capped at `CommitRetryMaxDelay` (5 s). Exhausted → `Sync.CommitRetriesExhausted` (exit 5); uploaded objects stay and a re-run reuses them. A share deleted meanwhile → the objects this run stored are deleted and `Shares.Deleted` (exit 8). A missing share is `Shares.NotFound` / `NameNotFound` (the CLI adds "Create it with: mysync push <name> --create, or mysync sync --name <name> in a folder"); a name without a manifest is `Shares.Incomplete`. Nothing to push (no file after skips) commits nothing: `Nothing to push.`, exit 0 (9 with skips).
- **`--create`** (a name, not an ID: `Push.CreateNeedsName`): if the name is free, claim it (`If-None-Match: *`), commit an **empty revision 1 at once**, then push as usual (revision 2). Claiming and committing straight away means other servers running `push --create` at the same moment find a share to push into, not an unfinished one; one that loses the claim pushes into the winner's share (and waits briefly if it finds the name before revision 1). **`--compress`** (with `--create`): the share this push creates starts with compression on, exactly as `sync --name <n> --compress` (revision 1 already format 3, `compression: gzip`), so this push's own uploads, the record included, are compressed by `CompressionPolicy`. For a share that exists (found, joined after losing the claim, or without `--create`) it changes nothing: the push uses the share's setting, and if that is off it warns `Compression is a setting of the share. Turn it on with: mysync compression on --share <name>`, as `sync --compress` does in a share. A dry run of `--create --compress` reports `gzip`.
- **The push record** `_mysync-push.json` (unless `--no-info`) is one ordinary file at the target folder's root: `formatVersion`, `host`, `user`, `os`, `mysyncVersion`, `startedUtc`, `finishedUtc`, `sources` (full paths), `target`, `files`, `bytes`, `skipped`, and with filters `filters` and `filteredOut` (as in `--json`; left out without filters, so an unfiltered record is byte-for-byte as before; `formatVersion` stays 1, nothing reads it strictly). Source-generated, deterministic (`PushRecordSerializer`, times to the second). Never secrets: no environment variables, no connection strings, no command line beyond the sources. A pushed file of that name at the target root wins, with a warning.
- **`--dry-run`** reads and hashes everything and checks what the share already stores, then prints the target folder and the counts (with filters: the cutoff, the files left out, and the list of files that would go). It uploads and commits nothing, but fails where a push would (`SourceIsTheShare`, `TargetIsAFile`, a missing share).
- **Output** (`Rendering/PushReportView`): warnings and skipped paths on stderr; `Pushed 214 files (1.3 GB; 187 uploaded, 27 already stored) to <share>`, the folder, with filters `changed since 2026-11-22 23:45 UTC · 203 files left out by filters (188 not changed, 14 excluded, 1 too large)`, the revision, and `Get them with:  mysync sync <share>`. Progress follows `SyncProgressDisplay`. Never prompts. **Never exits 3 or 7.**

## 10c. Downloading (`mysync pull`)

`pull` is a **read-only download** of a share, or folders and files of it, into an **ordinary folder**: the mirror image of `push`. It never uploads, commits or changes the share (read access is enough), never writes sync state (no `.mysynctool`, lock or conflict log; the destination stays an ordinary folder, and a later `mysync sync` there has nothing to do with the pull), and never deletes or silently replaces a local file. It works on any share: one people sync, one servers push into, or both.

```text
mysync pull <name|shareId> [<share-path>...] [--to <folder>] [--latest] [--host <name>]... [--overwrite] [-n|--dry-run] [--json] [--parallel <n>]
```

- **One revision per pull:** HEAD is read once, and everything comes from it, even if the share changes meanwhile.
- **Where things land** (`PullSelection`, the mirror of `PushSourceLayout`): no paths → the share's **contents** under the destination; a folder → `<dest>/<its name>/…`; a file → `<dest>/<its name>`. A path that is neither a file nor a folder with entries in HEAD (`web0` is not `web01`) → `Pull.PathNotFound`; two landing at one name → `Pull.DuplicateTarget` (both exit 2, before anything is downloaded). `dir` entries become empty folders. Share paths are typed with `/` (`\` on Windows becomes `/`, a leading `./` is dropped: `SharePathArgument`).
- **The share's `.mysyncignore` / `.gitignore` are not applied:** a pull takes exactly what was asked for, and a `.mysyncignore` in the share is downloaded as an ordinary file.
- **`--latest`** takes, for each host folder at the share root (a top-level folder with a child named like a push folder, `PushTarget.TryParseFolderName`, which also gives the `-n` number so two pushes of one second order correctly), its **newest** push folder, landing at `<dest>/<host>/<timestamp>/…`. `--host` (repeatable, only with `--latest`: `Pull.HostNeedsLatest`) narrows it, matched ignoring case; a host without a push folder → `Pull.HostNotFound` (exit 2). `--latest` with share paths → `Pull.LatestWithPaths` (exit 2). No push folders at all → `Pull.NoPushes` (exit 8). The CLI gathers repeated `--host` into one JSON array, as push does its patterns (`KnownCommands.RepeatableOptions`).
- **The destination** (`--to`, relative to the current folder; default the current folder) is opened through `ILocalFolderFactory.OpenPullDestination`: an ordinary `LocalFolder` with the same `PathResolver` rules (NFC, nothing outside the root, a symlink on the way refused), staging in `.mysync-pull.tmp/`. It is created by the first write. It learns case sensitivity **without writing** (an existing folder on the way looked up with its case flipped), so a dry run writes nothing.
- **Checked before anything is downloaded**, for every path the pull would write (`ILocalFolder.GetEntryKindAsync`, then size and SHA-256): nothing → download; the same content → left alone ("already there"; so a re-run finishes an interrupted pull); different content → **in the way**, unless `--overwrite` (replace it); a folder where a file goes, a file where a folder goes (also on the way), or a symlink → in the way **even with** `--overwrite`. Anything in the way fails the whole pull: `Pull.DestinationOccupied(count, first five)` (exit 4). A file that can't be read for the comparison counts as different. Files the selection doesn't write are never touched or listed.
- **A destination that syncs with the same share is refused** (`Pull.DestinationIsTheShare`, exit 4, nothing overrides it): `ILocalSources.FindSyncedFoldersAsync` finds a `.mysynctool` in it or a parent; pulled files would come back as new files on its next sync. Another share's folder is allowed, with a warning (`This folder syncs with <id>; pulled files will be uploaded to it by its next sync.`).
- **Downloading** reuses sync's path: `ObjectTransfers.DownloadToStagingAsync(…, into: destination)` decodes once through `ObjectCodec`, checks SHA-256 while streaming into staging, then `PlaceStagedFileAsync` moves it into place with the manifest's mtime and executable bit. `--parallel` / `MYSYNC_PARALLELISM`; progress as `SyncProgressDisplay`. Staging is emptied before and after (also on Ctrl+C, exit 130; files already in place stay, a re-run completes the rest).
- **Skipped, never fatal** (reported, exit 9): names Windows can't hold (on Windows), paths differing only in case on a case-insensitive disk (neither written), a destination file that can't be replaced (locked), an object the share no longer stores, a failed integrity check, and mysync's own file names (`StateFileNames.AlwaysIgnored`: a manifest is untrusted and must not plant a `.mysynctool`).
- **`--dry-run`** resolves the selection and runs every destination check, then reports what would be downloaded; nothing is written, not even the destination folder.
- **Output** (`Rendering/PullReportView`): `Pulled 12 files (84.1 MB) from <share>, revision 57` (with `--latest`: `(the newest push of 3 hosts)`), `into:` each landing folder relative to the current folder, already-there and replaced counts; warnings and skipped paths on stderr. Never prompts. **Never exits 3, 5 or 7.**

## 10d. The terminal interface (`mysync tui`)

`mysync tui` opens a full-screen, **keyboard-first** interface for **the one folder it was started in**: every file's status, Rescan, Sync with live progress, cancel, conflicts (keep mine / keep theirs, a diff), pushing one item of the folder to **another** share, help, quit. It is a **presentation layer over the existing handlers**: no sync, push or merge logic is repeated. Out of scope: pull, publish, delete, rename, gc, compression controls, a share list or folder picker, cloning, push filters or a host override, the mouse, file watching. **Nothing is scanned, synced or pushed unless a key asks for it** (one rescan at start).

### Start-up (`apps/mysync/Commands/TuiCommands.cs`)

In this order, each a plain CLI error before any screen: stdin or stdout redirected → `Tui.NotATerminal` (exit 2); smaller than 80 × 24 → `Tui.TooSmall` (exit 2); storage settings (`CheckStorage`, exit 2); the lock `.mysynctool.ui` (`IInterfaceLock`, exit 6 with the other PID when readable; taken **before** a share is created, so two can't both create one); then the folder: a share → open (`--name` / `--compress` ignored, noted in the activity list); not a share and no `--name` → `Shares.NameRequired` with the suggestion and "run `mysync clone <name>` first, then `mysync tui` in its folder" to work on an existing share instead (exit 2); `--name` → `InitializeEmptyShareCommand`: unsafe-root rule, the name claimed first (`ShareNameClaim`, shared with create; taken → exit 4), revision 1 **with no entries** (format 3 with `--compress`), `.mysynctool` with an empty base, **nothing uploaded**. A CLI `mysync sync` in the same folder still works (separate lock). Exit `0` on quit, `130` when a second Ctrl+C (or SIGTERM) ends it during a job. **The terminal is always restored** (`TuiRuntime.RunAsync`'s `finally`: alternate screen left, cursor shown, colours reset, autowrap and input mode back), also after an exception, which `CommandRunner` then prints. While the screen is open **nothing writes to stdout or stderr**: the log goes through `SwitchableLogSink` (`apps/mysync/Hosting`), held in memory, its warnings and errors shown in the activity list.

### The folder session (`libs/mysync/application/Session/`, front-end neutral)

- **`GetFolderStatusQuery`** is `status`'s preview (`SyncPipeline.PreviewAsync`, which now also hands back the scan's hashes, HEAD's author, compression and entry count as `SyncReport` init properties) mapped to **`FileStatus`** by `FolderStatusMapper`, the one place a plan becomes statuses: `NoChange` → synced; `Upload` / `UpdateRemoteMode` / `AddRemoteDirectory` → new-here (no base) or changed-here; `RemoveRemote` → deleted-here; `Download` / `UpdateLocalMode` / `CreateLocalDirectory` → new-in-share or changed-in-share; `DeleteLocal` → deleted-in-share; `Conflict` → conflict (with its kind); `Skip` → ignored (`IgnoredLocally`) or skipped (with the reason); scan-skipped paths the plan doesn't hold are skipped too. Entries carry size and mtime from the side that matters, and for share changes HEAD's author and revision (a conflict: the ones it recorded). It writes nothing; a caller-owned in-memory hash cache is merged in (theirs win) and handed back, never written. `FolderStatus.RecordedConflicts` (from `.mysynctool`) says which conflicts `resolve` can decide: a rescan finds new ones but records nothing, so a conflict only the rescan saw needs a sync first (which records it and leaves it).
- **`IFolderSession`** (one per process, `FolderSession`) holds the last status and runs **one job at a time** (`Session.Busy` otherwise): `Rescan`, `Sync(allowMassDelete, noDelete)`, `Resolve(paths | all, keep)`, `Push(path, share, create)`, `Cancel`, plus the read-only `DiffAsync`. Each job calls the existing command or query in its own scope (`.mysynctool` reloaded every time, so a CLI sync is picked up). After a Sync or Resolve finishes, a rescan follows; after a Push, none. Push takes one item **relative to the folder**, validated through `SharePath` (no `..`, nothing absolute), refuses the folder's own share by name or ID (`Session.PushToOwnShare`) and an item not on disk (`Session.NothingOnDisk`), and runs `PushCommand` with its defaults.
- **Events** (`SessionEvents`): job started / finished (with the status or report) / failed / cancelled, phase started, transfers planned, `TransferProgress` (cumulative bytes and speed, coalesced by `SessionProgress` to about 10 a second per direction), `FileStarted` (from the new optional `ISyncProgressReporter.FileStarted`, so the progress view can name the current file), `FileFinished`. `SessionEventQueue` never blocks a job: unbounded, and display-only events are dropped while a few are unread.

### Model–update–view (`libs/mysync/tui`)

`TuiState` (one immutable record: screen, rows, selection, scroll, filter, search, collapsed folders, the job's progress, dialogs, activity, banner, size; `StatusSummary` worked out once per rescan) ← `TuiUpdate.Update(state, msg) → (state, effects)`, **pure** (time arrives as `Tick`; no clock, no I/O). Messages: `KeyPressed`, `Resized`, `SessionMsg`, `Tick`, `DiffLoaded`, `Refused`, `LogLine`. Effects: `StartRescan`, `StartSync`, `StartResolve`, `StartPush`, `LoadDiff`, `CancelJob`, `Quit`. Views (`Screens.Render(state) → Frame`) are **pure** and draw **only the visible rows** (`ListModel` builds the tree when the status, filter, search or collapsed folders change, never per frame), so 100,000 entries stay smooth. `TuiRuntime` runs the loop: keys, events, ticks and resizes (the size is polled) into Update, effects against the session, at most ~10 frames a second during a job. `AnsiTerminal` is our own renderer on raw ANSI (**no package**): alternate screen, cursor hidden except in a text field, autowrap off, each frame **diffed** against the last so only changed cells are written, keys without echo (Ctrl+C as a key), virtual-terminal processing on Windows (`SystemConsole`, `LibraryImport`).

### Theme, symbols, layout

- **Colours** (`Theme`; `TerminalDetection.Colors`: `NO_COLOR` / `--no-color` → none; `COLORTERM` truecolor/24bit or `WT_SESSION` → 24-bit; a `256color` `TERM` → 256; Windows otherwise 24-bit; else 16): `Background` #161514 (16-colour: not painted), `Text` #E7E5E4, `Muted` #A8A29E (bright black), `Dim` #57534E, `Rule` #45423F, `SelectionBg` #33312F (reverse), `Here` #FDBA74 (yellow), `Share` #93C5FD (bright blue), `Conflict` #FCA5A5 (bright red), `Ok` #86EFAC (bright green), `KeyCap` #161514 on #A8A29E (reverse), `Chip` #161514 on #E7E5E4 (reverse), `DialogBg` #292524, `FieldBg` #161514 (reverse), `BarEmpty` #57534E. 256-colour mode uses the nearest xterm-256 colour; no colour keeps bold and reverse video, and **every status has a symbol and a word**, never colour alone. One dark theme.
- **Symbols** (`Symbols`; ASCII with `--ascii` or when the output isn't UTF-8): ↑ `^`, ↓ `v`, − `-`, ! `!`, ✓ `=`, · `.`, ▸ `>`, ▾/▸ `v`/`>`, ─ `-`, box `┌─┐│└┘` `+-+|++`, bar █/░ `#`/`.`, … `~`.
- **Main screen** (W × H): row 0 `mysync · <share>` and the folder path (cut from the left); 1 `share revision N · last changed by <who>` and `scanned <when>` / `scanning…` / `syncing…`; 2 rule; 3 the summary (`↑ N to upload`, `↓ N to download`, `− N to delete`, `! N conflicts`, `N skipped`; `✓ Everything is in sync`; a new share: `This share is new and empty.` / `N files here aren't in the share yet. Press s to upload them.`), cut with `…`; 4 rule; 5 `Show:` and the six filters with counts (the active one a chip), `/ search`; 6 the banner; 7 headings; 8 … H−4 the list; H−3 `<shown> of <total> rows` and `storage <account>/<container> · gzip`; H−2 rule; H−1 the key bar (key caps, dropped from the right, `?` always kept). Columns (`Screens.Columns`): 1 space, a 2-cell marker, Name (W−60, at least 20), Status 22, Size 8 (right-aligned), 2 spaces, Details 24; below 100 columns Details is dropped and Name takes its width. Folders first, then names, ordinal; files indented 2 per level; folders `▾`/`▸` with a rollup. Below 80 × 24 only `Make the window at least 80 × 24` (and `q`).
- **The progress view** (a sync, resolve or push): the title and `step N of 4: …`, one bar per direction (46 cells, shrinking to 20) with percent, files, bytes and speed, the current file under it, then the job's items with their state; `Ctrl+C also cancels safely…`; `Esc  Cancel`. When it ends: the banner (`✓ Sync finished  2 uploaded · 2 downloaded · 2 deleted · the share is at revision 43`, or the error's text) and an activity line.
- **Conflicts** (`c`): each conflict, the highlighted one with `Yours, in this folder` / `Theirs, in the share` (size, modified, who; `deleted` for a missing side), then **This session** (the activity list). `m` / `t` / `M` / `T` / `d` / `Esc`. **The diff** (`d`): `DiffQuery`'s result, scrollable, removed lines `Conflict`, added `Ok`, hunk headers `Share`, notes; control characters shown as placeholders (`TuiUpdate.Visible`). **Dialogs**: 64 wide from row 3, everything else `Dim`. Push: the share-name field (40 cells, the cursor shown), the "create" checkbox (off), the landing folder (`PushTarget`), errors under the field (the `ShareReference` error, or the folder's own share); `Enter` does nothing while empty or invalid. Confirmations (mass delete after `Sync.MassDeleteBlocked`, a large first upload above the CLI's thresholds, quitting during a job): `y` acts, `n` / `Esc` cancel, **`Enter` alone does nothing**. Help (`?`) lists every key.
- **Keys**: `↑↓`/`kj`, PgUp/PgDn, Home/End, `→`/Enter opens a folder, `←` closes it (on a file: to its folder), `r` rescan, `s` sync, `p` push, `c` conflicts, `f`/`F`/`1`–`6` filters, `/` search (Enter keeps, Esc clears), `?` help, `Esc` back / cancel a job, `q` quit (asks during a job), Ctrl+C cancels the job (a second one quits with 130), with no job quits. Keys that would start a job while one runs: `Wait for the <job> to finish, or press Esc to cancel it.` A failed rescan (share deleted, storage unreachable, credentials, upgrade needed) is the banner; `s` and `p` show it again; `r` retries.

---

## 11. Storage abstraction (`MySync.Application/Abstractions/Storage/`)

```csharp
public interface IShareStore
{
    Result<StorageLocation> GetLocation();                                                                   // provider, account, container (no secrets)
    Task<Result> EnsureReadyForNewShareAsync(CancellationToken ct);                                         // e.g. create container; idempotent
    Task<Result<ManifestSnapshot?>> GetManifestAsync(ShareId shareId, CancellationToken ct);                 // null value = share doesn't exist (also before "ready")
    Task<Result<string>> CreateManifestAsync(ShareId shareId, Manifest manifest, CancellationToken ct);      // If-None-Match: *; AlreadyExists; returns ETag
    Task<Result<string>> ReplaceManifestAsync(ShareId shareId, Manifest manifest, string ifMatchETag, CancellationToken ct); // PreconditionFailed on 412, and when HEAD is gone
    Task<Result> PutManifestHistoryAsync(ShareId shareId, Manifest manifest, CancellationToken ct);         // already stored = success
    IAsyncEnumerable<Result<Manifest>> ListManifestHistoryAsync(ShareId shareId, CancellationToken ct);     // oldest first
    Task<Result> DeleteManifestHistoryAsync(ShareId shareId, Revision revision, CancellationToken ct);      // gc; missing = success

    Task<Result<bool>> ObjectExistsAsync(ShareId shareId, ContentHash hash, CancellationToken ct);
    Task<Result> UploadObjectAsync(ShareId shareId, ContentHash hash, ObjectUpload upload, CancellationToken ct); // stored as given + Content-Encoding; Verify() before commit; raw also hash-checked; existing (either form) = success
    Task<Result<StoredObject>> OpenObjectReadAsync(ShareId shareId, ContentHash hash, CancellationToken ct); // stored bytes + encoding + length, never decoded; NotFound; UnsupportedEncoding
    IAsyncEnumerable<Result<StoredObjectInfo>> ListObjectsAsync(ShareId shareId, CancellationToken ct);     // canonical object names only
    Task<Result<int>> CountRecoverableCopiesAsync(ShareId shareId, CancellationToken ct);                  // after delete: soft-deleted / versioned copies left
    Task<Result> DeleteObjectAsync(ShareId shareId, ContentHash hash, CancellationToken ct);                // missing = success

    // Publishing (§10a)
    Task<Result<CopySource?>> GetCopySourceAsync(ShareId shareId, ContentHash hash, TimeSpan lifetime, CancellationToken ct); // a readable URL and/or the object natively; null = stream
    Task<Result<PublicationsSnapshot?>> GetPublicationsAsync(ShareId shareId, CancellationToken ct);       // null value = none yet
    Task<Result<string>> PutPublicationsAsync(ShareId shareId, PublicationRegistry registry, string? ifMatchETag, CancellationToken ct); // null ETag = If-None-Match: *; else If-Match; PreconditionFailed
    Task<Result> DeletePublicationsAsync(ShareId shareId, CancellationToken ct);                           // missing = success
}

public sealed record ManifestSnapshot(Manifest Manifest, string ETag);
public sealed record PublicationsSnapshot(PublicationRegistry Registry, string ETag);
public sealed record StoredObjectInfo(ContentHash Hash, long Size, DateTimeOffset LastModifiedUtc);
```

- **Every method returns `Result` / `Result<T>`.** The provider translates its SDK's failures into `StorageErrors` (`NotFound`, `PreconditionFailed`, `AlreadyExists`, `Unauthorized`, `Unreachable`, `Timeout`, `ClockSkew`, `TooLarge`) and `ConfigurationErrors`. Application code never sees `Azure.RequestFailedException` or `AmazonS3Exception`. Only `OperationCanceledException` from the caller's own token propagates.
- Azure-specific retries use the SDK's built-in retry policy (exponential, ~5 tries). The pipeline's own retry is only for manifest precondition failures.
- `AzureBlobShareStore` gets `IManifestSerializer` injected. It never serializes manifests itself.
- **Objects carry an encoding** (`ObjectUpload`, `StoredObject`). `UploadObjectAsync(shareId, hash, ObjectUpload)` stores the bytes as given (raw or gzip) with that `Content-Encoding`; `size` metadata stays the **uncompressed** size, and a gzip object also gets `storedsize`. No length up front and no seekable stream: the Azure store stages blocks as it reads (nothing visible until the block list is committed), so a compressed upload streams through without a temporary file. **Integrity:** after the last byte the store calls `ObjectUpload.Verify` (the producer hashed the uncompressed bytes in the same pass that fed the compressor) and commits only if it succeeds; for raw bytes it also checks SHA-256 and length itself (`Storage.HashMismatch`). `OpenObjectReadAsync` returns the **stored** bytes, their encoding and length, never decoded; an unknown `Content-Encoding` is `Storage.UnsupportedEncoding` (also in listings, which report each object's encoding and stored length).
- **The Azure SDK doesn't decode `Content-Encoding: gzip`** (checked 2026-11-22, Azure.Storage.Blobs): a GET answers with that header and the client still returns the compressed bytes, and `OpenReadAsync` reads ranges of the blob as stored. Pinned by the contract case `AGzipObject_Should_ReadBackAsStored_NotDecoded` and the Azure-only `AGzipObject_Should_CarryContentEncoding…`, so a change in the SDK fails a test rather than decoding twice.
- Azure details (`libs/mysync/storage-azure`): a missing container or blob reads as "no share" (`GetManifestAsync` → null, listings → empty), never an error. `ReplaceManifestAsync` maps both 412 and 404 to `PreconditionFailed`, so the pipeline re-reads. `AzureErrorMapper` maps 401/403 → `Unauthorized`, 404 → `NotFound`, 408 and network timeouts → `Timeout`, 409 → `AlreadyExists`, 412 → `PreconditionFailed`, no response or 5xx after retries → `Unreachable`. No `Error` carries SDK text; the SDK's message goes to the debug log through `ConnectionStringRedactor`. `AzureTransferSettings` (block size, retries, timeout) is fixed in production; tests use small blocks and fewer retries.
- `MySync.Testing` provides an `InMemoryShareStore` that enforces ETag semantics exactly like Azure (`If-Match`, `If-None-Match: *`). Most pipeline tests run against it. Its behaviour is pinned against real Azure and real S3 by the shared contract-test suites (§14).

### S3 differences (`libs/mysync/storage-s3`)

AWS S3 general purpose buckets only, through `AWSSDK.S3`; **S3-compatible services are not supported** and there is no endpoint setting. The layout is `ShareLayout`'s, key for key. Where copying the Azure store's logic would be wrong:

- **Conditional requests have three outcomes.** 412 is the answer (`AlreadyExists` for a create, `PreconditionFailed` for a replace or delete; never retried). **409 ConditionalRequestConflict on PutObject** means a concurrent request got in the way and the outcome is undecided: the same PutObject is retried with back-off (`S3TransferSettings.ConditionalConflictAttempts`, then `Unreachable`), so a 409 never reaches the commit loop as a lost race. **409 on CompleteMultipartUpload** kills the upload: it is aborted, the object HEADed (a competing upload of the same content may have stored it: success), and the upload restarted from the start of the content when it can be read again, else `Unreachable` (the next run uploads it). **404 on an `If-Match` write or delete** (the key is gone) is `PreconditionFailed`, as on Azure. Conditional deletes (aliases) are `DeleteObject` with `If-Match`, which needs `s3:GetObject` as well as `s3:DeleteObject`.
- **ETags follow the content, not the write.** Identical bytes get the same ETag (pinned by `IdenticalBytes_Should_KeepTheirETag_OnS3`), and a metadata-only change keeps it. Every `If-Match` decision therefore depends on the body alone: a manifest's bytes change with every commit (the revision; `ManifestSerializerTests.EveryCommit_Should_ChangeTheBytes…`), `publications.json` changes with every record change, and **an alias's body holds the share ID** (and when and who), so a name re-pointed at another share has another ETag and a stale conditional delete can't remove it (`AliasStoreContractTests.AnAliasRecreated_ForAnotherShare…`). The body is the source of truth (`GetAsync` reads it); the `x-amz-meta-*` copy is for listing. An alias deleted and recreated for the same share by the same person at the same instant keeps its ETag; a stale delete then removes an alias saying exactly what the caller read, which is accepted. ETags are opaque: passed back as S3 returned them, quotes included, never read as an MD5.
- **A listing returns neither metadata nor `Content-Encoding`:** `S3AliasStore.ListAsync` is one ListObjectsV2 of `aliases/` plus a HEAD per alias, and `S3ShareStore.ListObjectsAsync` a HEAD per object, at most `MYSYNC_PARALLELISM` at once. `aws-chunked` in a returned `Content-Encoding` (an upload's framing) is ignored.
- **403 can mean "missing".** Without `s3:ListBucket`, S3 answers 403 for a key that doesn't exist. The documented policy grants it, so on the share bucket 404 means missing and a 403 on a read is `Unauthorized` with that hint, never "not found". Anonymous readers of the publishing bucket never have it: `NotServedStatusCodes` is {403, 404}.
- **Uploads of unknown length** (gzip): up to one part is read; if the content ends inside it, one conditional PutObject; else a multipart upload, part by part, holding one part in memory, with `If-None-Match: *` on the completion and the hash check before it (`S3Uploader`). Part sizes (`S3PartSizes`): known length `max(16 MiB, ceil(length / 10,000))`; unknown length starts there for the uncompressed size and doubles every 1,000 parts up to 1 GiB, about 4.87 TiB in 10,000 parts from 16 MiB; past that `Storage.TooLarge`, never an SDK error at part 10,001. A failed or cancelled multipart upload is aborted (best effort; the setup guide's lifecycle rule removes what can't be). Limits in `S3Limits` (5 MiB–5 GiB parts, 10,000 parts, 5 TiB objects, 5 GiB PutObject/CopyObject).
- **Credentials** are resolved **once, on first use** (`S3Connection`), so `--help`, `--version` and `self-check` never touch them: keys in the environment; else the `AWS_PROFILE` (or `default`) profile through `CredentialProfileStoreChain`, of any kind (the SDK's default chain refuses SSO profiles); else the rest of the chain (web identity, ECS, the EC2 instance role, which fails within about a second off EC2; `AWS_EC2_METADATA_DISABLED` skips it). Nothing found is `Configuration.Missing` listing the sources; an expired or never-started SSO session is `Unauthorized` with "run `aws sso login`"; an SSO portal or STS out of reach is `Unreachable`. `AWSSDK.SSO`, `AWSSDK.SSOOIDC` and `AWSSDK.SecurityToken` are referenced because the SDK loads them by name for those profiles (checked in the published single-file binary). The credential scope (`aws:` + a hash of the access key ID) decides native copies.
- **Wrong region and clock:** redirects are never followed (`AllowAutoRedirect = false`); 301 `PermanentRedirect`, 400 `AuthorizationHeaderMalformed` / `IllegalLocationConstraintException` are `Configuration.WrongRegion`, naming the bucket, the configured region and the right one when the body says (`<Region>`, `<Endpoint>`). The SDK's clock-skew correction stays on; a `RequestTimeTooSkewed` that still surfaces is `Storage.ClockSkew` ("off by about N minutes", exit `2`), not `Unauthorized`.
- **Versioning:** `CountRecoverableCopiesAsync` counts non-current versions and delete markers under the share's prefix when the bucket's versioning is on or suspended (0 when off); `delete` then warns.
- **Checksums** stay on (the SDK's defaults). **No ACL is ever sent.** Every published object has an explicit `Content-Type`. Redirects, ACLs and bucket settings are never changed by mysync; `.mysynctool` records `s3`, the region and the bucket.
- **Copy sources:** `GetCopySourceAsync` gives the object natively (`NativeObjectReference`: `s3`, region, bucket, key, credential scope) and a presigned GET (at most an hour; it dies with temporary credentials that signed it).
- **Redaction:** `S3Redactor` masks `AWS_SECRET_ACCESS_KEY` / `AWS_SESSION_TOKEN` values (and their config-file spellings) and a presigned URL's `X-Amz-Signature`, `X-Amz-Credential` and `X-Amz-Security-Token`; the CLI applies it with `ConnectionStringRedactor` to anything unexpected.

**S3 error mapping (`S3ErrorMapper`, the one place):**

| S3 | mysync |
|---|---|
| 412 PreconditionFailed | `PreconditionFailed` (`AlreadyExists` for an `If-None-Match` create) |
| 409 ConditionalRequestConflict | PutObject: retried; CompleteMultipartUpload: aborted and restarted (above); left over: `Unreachable` |
| 503 SlowDown, 500, timeouts, connection failures | the SDK's retries, then `Unreachable` / `Timeout` |
| 404 NoSuchKey (HEAD: a body-less 404) | `NotFound` (or null / false / `PreconditionFailed` where the method says so) |
| 404 NoSuchBucket | `Configuration.BucketNotFound`, naming the bucket and region |
| 403 AccessDenied | `Unauthorized`, with the `s3:ListBucket` hint on reads and the `s3:GetObject` hint on conditional deletes |
| 403 InvalidAccessKeyId, SignatureDoesNotMatch; ExpiredToken | `Unauthorized` (with the `aws sso login` hint for an expired session) |
| 403 RequestTimeTooSkewed | `Storage.ClockSkew` (exit 2) |
| 301 PermanentRedirect, 400 AuthorizationHeaderMalformed, IllegalLocationConstraint | `Configuration.WrongRegion` |

---

## 12. .NET conventions and tooling

- **.NET 10**, C# with `ImplicitUsings` and `Nullable` enabled solution-wide (`Directory.Build.props`).
- **Warnings are errors** (`TreatWarningsAsErrors`, `AnalysisMode=All`, SonarAnalyzer, `EnforceCodeStyleInBuild`). Code must build clean. Fix analyzer and style warnings; don't suppress them casually.
- **Central Package Management:** every version lives in `Directory.Packages.props`. Add `<PackageReference Include="..." />` (no version) in the csproj and a `<PackageVersion ... />` entry centrally.
- Prefer **primary constructors**, `sealed` classes, file-scoped namespaces, target-typed `new` and collection expressions (`[]`).
- `internal` for handlers, validators, decorators, adapters and serializers. `public` only for what other assemblies genuinely need (commands, queries, responses, abstractions, domain types). Each library grants `InternalsVisibleTo` to its test project. Keep that pairing when adding projects.
- `async` all the way down, with a `CancellationToken` on every async method. Because commits are atomic, a cancelled run is always safe to re-run: stop new transfers, never commit a half-planned manifest, clean the staging dir.
- Stream everything. Hash with an 81920-byte (or larger) buffer. Never `File.ReadAllBytes` a user file.
- Bounded parallelism with `Parallel.ForEachAsync` / `Channel<T>`. Hash and transfer in parallel, but keep the manifest build single-threaded and deterministic.
- No static mutable state. Time comes from `IDateTimeProvider`; the disk from `ILocalFolder`. Waits (the commit back-off) go through the BCL `TimeProvider`, which tests replace with `InstantTimeProvider` (fires at once, records each delay).
- All JSON (manifest, state, `--json` output) goes through **source-generated** `JsonSerializerContext`s. No reflection serialization.
- Ordinal string comparison (`StringComparer.Ordinal`) for manifest keys, everywhere.
- Locked or unreadable local files (common on Windows): skip that path with a warning, report it in `SyncReport.Skipped` (exit `9`), and keep its base entry unchanged. Never abort the whole run for one file.

---

## 13. Packages (`Directory.Packages.props`)

| Package | Used by | Purpose |
|---|---|---|
| `ConsoleAppFramework` | `MySync.Cli` | Command routing and argument binding (source-generated) |
| `Microsoft.Extensions.Hosting` | `MySync.Cli` | Host, DI, configuration (env vars only), logging |
| `Spectre.Console` | `MySync.Cli` | Tables, progress bars, the single `y/N` prompt |
| `ConsoleAppFramework.Abstractions` | `MySync.Cli` | Attributes/types used alongside the generator |
| `Serilog`, `Serilog.Extensions.Hosting`, `Serilog.Extensions.Logging`, `Serilog.Sinks.Console` | `MySync.Cli` (`Serilog` also `MySync.Application`, for `LogContext`) | Logging to **stderr** |
| `Scrutor` | `MySync.Application`, `MySync.Infrastructure` | Assembly scanning + `Decorate(...)` |
| `FluentValidation`, `FluentValidation.DependencyInjectionExtensions` | `MySync.Application` | Command validators (`AddValidatorsFromAssembly`, including internal types) |
| `Microsoft.Extensions.DependencyInjection.Abstractions`, `Microsoft.Extensions.Options`, `Microsoft.Extensions.Logging.Abstractions` | libraries | `DependencyInjection.cs` extensions, options, `ILogger<T>` |
| `Azure.Storage.Blobs` | `MySync.Storage.Azure` **only** among libraries (tests and `MySync.Testing` may use it) | Blob provider; the publish target (`Put Blob From URL`, `Put Block From URL`, service properties) |
| `AWSSDK.S3` | `MySync.Storage.S3` **only** among libraries (tests and `MySync.Testing` may use it) | S3 provider and publish target: conditional PutObject / CompleteMultipartUpload / DeleteObject, multipart, CopyObject, presigning. One release set with the three below (2026-11-10, `AWSSDK.Core` 4.0.103); +1 MB on the `linux-x64` binary |
| `AWSSDK.SSO`, `AWSSDK.SSOOIDC`, `AWSSDK.SecurityToken` | `MySync.Storage.S3` | Never called directly: the SDK loads them by name for SSO, assumed-role and web-identity profiles, which otherwise fail at runtime |
| (none) | `MySync.Updates.GitHub` | `update` uses the BCL only: `HttpClient`, `System.Text.Json`, `System.Formats.Tar`, `ZipFile`, `ECDsa` |
| `SonarAnalyzer.CSharp` | all (via `Directory.Build.props`) | Analyzers |
| `xunit.v3` | tests | Test framework, on **Microsoft.Testing.Platform** |
| `Shouldly` | tests | Assertions |
| `NSubstitute` | tests | Substitutes for abstractions in handler tests |
| `xunit.v3.extensibility.core` | `MySync.Testing` | `[Fact]` and `TestContext` for the abstract contract suites, without making the library a test project |
| `xunit.v3.assert` | `MySync.Testing` | `SkipException`: a test whose storage isn't configured skips (`TestStorage`) |
| `Microsoft.Extensions.DependencyInjection` | tests only | A concrete `ServiceCollection` for DI tests (the libraries reference only the abstractions) |
| `NetArchTest.Rules` | architecture tests | Dependency rules (§3) |
| `JsonSchema.Net` | infrastructure tests only | Validates the `site-data.js` mysync writes against `apps/mysync-site/contract/site-data.schema.json` |

**npm** (root `package.json`, one `package-lock.json`; only `apps/mysync-site` uses them):

| Package | Kind | Purpose |
|---|---|---|
| `react`, `react-dom` | runtime | The publication page (bundled into every publication's `app.js`) |
| `@tanstack/react-virtual` | runtime | Virtualized file list (100,000 entries) |
| `nx`, `@nx/react`, `@nx/vite`, `@nx/vitest`, `@nx/eslint`, `@nx/js`, `@nx/web` | dev | Workspace tooling (same version as `nx`) |
| `vite`, `@vitejs/plugin-react`, `typescript` | dev | Build and type checking |
| `vitest`, `jsdom`, `@testing-library/react`, `@testing-library/dom`, `@testing-library/user-event` | dev | Site tests |
| `ajv`, `ajv-formats` | dev | Validating the fixture against `site-data.schema.json` |
| `eslint` (+ Nx's flat configs) | dev | Lint; `--max-warnings=0`, `dangerouslySetInnerHTML` banned |

A new **runtime** npm package ships inside every publication: it needs a reason, and `THIRD-PARTY-NOTICES.txt` picks up its license automatically (the build fails if it has none).

Don't add a package without a row here. Use the BCL (`IncrementalHash`, `System.Text.Json`, `File.SetUnixFileMode`) where it's enough. There is deliberately no gitignore package: neither candidate matched git (§7), so `.mysyncignore` matching is our own code, checked against git.

---

## 14. Testing

Every test project is xUnit v3 on the Microsoft Testing Platform with Shouldly, registered in `mysync.slnx`, and tagged in its `project.json` (§2).

| Project (path) | Tag | Tests | Fakes / backing |
|---|---|---|---|
| `MySync.Domain.UnitTests` (`tests/mysync/domain-unit-tests/`) | `type:unit-test` | `MergePlanner`, `SharePath` / `PathRules`, `MassDeleteGuard`, `ShareLayout`, value objects, `CloneTarget` | none (pure) |
| `MySync.Application.UnitTests` (`tests/mysync/application-unit-tests/`) | `type:unit-test` | Handlers, validators, `SyncPipeline`, `RemoteCommitter`, decorators | `InMemoryShareStore`, `InMemoryLocalFolder`, NSubstitute for single-failure cases |
| `MySync.Infrastructure.UnitTests` (`tests/mysync/infrastructure-unit-tests/`) | `type:unit-test` | `LocalFolderContractTests` on disk, `LocalFolder`, `FileScanner`, `Sha256ContentHasher`, hash cache, `JsonShareStateStore`, `FolderLock`, `FileConflictLog` rotation, `GitIgnoreMatcher` (against `git`), `ManifestSerializer` (exact JSON pinned), `PublicationRegistrySerializer` (pinned), `SiteDataContractTests` (generated `site-data.js` against the site's JSON Schema, escaping), `DownloadScriptExecutionTests` (the generated `download.sh` run with real `sh` + `curl` on hostile names), `PatchAppliesWithGitTests` (every `diff` patch turns A into B with `git apply`), `GzipDownloadScriptTests` (both download scripts against a local HTTP server serving gzip, with and without the header; `pwsh` when installed), `LocalSourcesTests` (push's reader on disk: excludes at any depth, symlinks, a file shared with its writer, `.mysynctool` in parents), `PushRecordSerializerTests` (pinned JSON, with and without filters), `DiskPullTests` (pull onto a real disk: mtimes and modes, `--to`, a re-run by content, leftover staging, a symlink in the destination stopping the pull with nothing written outside, the same-share refusal, a dry run writing nothing, the read-only case probe agreeing with the writing one), `DiskCloneTests` (`clone` on a real disk through the real `FolderCloneRunner`: the folder for a name, an ID with one, several or no names, relative and absolute folders, every row of the target table, unresolvable shares creating nothing; the clean-up after a failure, an exception and a cancellation, an existing target kept, something written meanwhile left and reported, an undeletable file reported after the clone's error, skipped paths keeping everything), and `DiskSyncTests`: the real pipeline over two temp folders with `InMemoryShareStore` | real temp directories (`TempShareFolder`), `git`, `sh`, `curl` |
| `MySync.Storage.Azure.IntegrationTests` (`tests/mysync/storage-azure-integration-tests/`) | `type:integration-test` | `AzureBlobShareStore` against the **real Azure test account**, plus the `IShareStore` **contract suite**; `AzureAliasStore` with the `IAliasStore` contract suite and Azure-only cases (blob shape, damaged and stray aliases, no container; a new ETag for identical bytes); `AzureStaticWebsitePublishTarget` on the test publishing account (preflight, headers stored, `If-None-Match` root files, server-side copies by Put Blob From URL and, with its limit lowered, by blocks; prefix delete, configuration errors, no key in errors or logs; a real static website serving gzip files that every client decodes); the static-website rules and configuration parsing offline | real Azure (`MYSYNC_TEST_AZURE_*`) |
| `MySync.Storage.S3.UnitTests` (`tests/mysync/storage-s3-unit-tests/`) | `type:unit-test` | Against a substitute `IAmazonS3`: every row of the S3 error mapping and its hints, the 409/412/404 outcomes per operation, the single-part shortcut, multipart uploads (part order, the verdict after the last byte, abort on failure and on cancellation, the restart after a 409 on completion, `TooLarge` before part 10,001), part-size arithmetic at the limits, alias listing with the bounded HEAD fan-out (damaged, stray and vanished aliases), batch-delete partial failure, native and multipart copies, the preflight's order against a stub anonymous web (probe served or not, a public listing), credential errors (none, SSO without a token, unreachable portal), redaction | NSubstitute, a stub `HttpMessageHandler` |
| `MySync.Storage.S3.IntegrationTests` (`tests/mysync/storage-s3-integration-tests/`) | `type:integration-test` | The `IShareStore` and `IAliasStore` **contract suites, unchanged**, against the **real S3 test bucket** (each store under its own key prefix); S3-only cases (a three-part upload, gzip multipart read back as stored, identical bytes keeping their ETag, wrong region, a bucket that doesn't exist, wrong keys with no secret in any log, a presigned URL readable anonymously, damaged and stray aliases, the alias body's shape); `S3PublishTarget` on the public test bucket (the preflight's probe and listing check for real, headers served anonymously, CopyObject with replaced metadata, a multipart copy with the threshold lowered to 5 MiB, prefix delete) | real S3 (`MYSYNC_TEST_S3_*`, AWS credentials) |
| `MySync.EndToEndTests` (`tests/mysync/end-to-end-tests/`) | `type:e2e-test` | Full DI graph (Application + Infrastructure + the provider) with temp folders as "machines", **each flow written once against a `Backend` and run once per provider** (`AzureBackend`: a container per test; `S3Backend`: a key prefix per test): `SyncTests` (create, clone, two-way sync, concurrent commits, a multi-part file, a conflict, the state file, a mismatch, delete), `CompressedShareTests` (a compressed share across two machines: blob properties, multi-block gzip uploads, conflicts, compression off again, byte-identical folders); `Cli/ShellQuoteTests`, `Cli/TerminalTextTests` and `Cli/CloneLocationTests` unit-test those BCL-only CLI helpers (compiled in as linked source); `Cli/CliTests` runs the **built `mysync` as a process** (`CliProcess`, fresh environment, stdin closed) for exit codes, stdout/stderr, `--json` and the no-terminal prompt rule; `PublishingTests` publish, unpublish and delete with the real embedded site, checked anonymously through the base URL, for Azure→Azure, S3→S3 (native copies), Azure→S3 (streamed) and S3→Azure (Put Blob From URL from presigned URLs); `PushTests` (two servers pushing at once from ordinary folders, the laptop's sync getting both byte for byte, deleting a pushed folder, `--create`, a folder of another share, a folder of the target share refused, a second `--updated-since last` push holding only the changed files, `--include` + `--max-size`) and `Cli/PushCliTests` (option order, plain output, `--json`, dry run, exit codes 0/2/4/8/9; repeated `--exclude` in any position, patterns with brackets and commas, every invalid filter value, the filter line, `filters` / `filteredOut`; `--create --compress` making a format-3 share a laptop clones byte for byte, `--compress` on an existing share warning); `PullTests` (a collector pulling `--latest` from two servers, then one folder with `--to`; an ordinary share pulled without changing its HEAD; a pull into the share's own folder refused) and `Cli/PullCliTests` (option order, repeated `--host`, `--json`, a re-run, dry run, exit codes 0/2/4/8/9); `TuiTests` (the runtime with a scripted terminal on the real composition: upload, another machine's change downloaded, a conflict recorded by a sync and resolved with `m`, a folder pushed with `--create` to a second share that a third machine pulls), `Cli/TuiCliTests` (no terminal → 2; in a pseudo-terminal via `script`: too small → 2, no `--name` → 2 and no screen, a taken name → 4, a second instance → 6) `Cli/SwitchableLogSinkTests` (nothing logged to the console while held) `Cli/S3CliTests` (the built binary with `MYSYNC_PROVIDER=s3`: the other provider's variables ignored either way, a missing bucket or region, a folder of one provider refused under the other with `State.StorageMismatch` and nothing changed, and a create / clone / sync / info / shares / delete round trip on the real bucket) and `Cli/CloneCliTests` (`clone <name>` creating the folder and a sync there both ways, `clone <id> <folder>` with the quoted `cd` hint, the current folder, a second clone and a non-empty folder refused, `--json`'s `folder` / `createdFolder`, exit codes 0/2/4/8, `sync <share>` unchanged) | real Azure and S3 (the CLI-process tests on Azure, `S3CliTests` on S3) |
| `MySync.Tui.UnitTests` (`tests/mysync/tui-unit-tests/`) | `type:unit-test` | `UpdateTests` (every screen's keys, filters, search, dialogs, confirmations, session events, resize), `ViewSnapshotTests` (every screen at 100×30 and 80×24, Unicode and `--ascii`, text and styles compared with `Snapshots/*.txt`; a missing one is written and fails; `MYSYNC_UPDATE_SNAPSHOTS=1` rewrites), `LayoutTests` (columns, cutting, key hints, 100,000 entries drawing in the same time), `AnsiTerminalTests` (bytes: alternate screen, diffed drawing, every colour depth, the cursor, keys, detection), `RuntimeTests` (whole sessions with `ScriptedTerminal` and the real session over the in-memory fakes: rescan → sync with progress → resolve → push → quit; Ctrl+C twice → 130; a throwing view and a stop request still restore the terminal; resizes; a new empty share) | in-memory fakes, `ScriptedTerminal` |
| `MySync.ArchitectureTests` (`tests/mysync/architecture-tests/`) | `type:architecture-test` | The dependency rules in §3 | none |
| `MySync.SiteAssets.UnitTests` (`tests/mysync/site-assets-unit-tests/`) | `type:unit-test`, `requires:site-build` | The embedded set is exactly the four site files plus the three root files, `index.html` loads `./site-data.js` then `./assets/app.js`, content types, the version digest, placeholder detection, and the embedded bytes equal `dist/apps/mysync-site` | the real site build (fails with advice if the placeholder is embedded) |
| `mysync-site` (`apps/mysync-site`, Vitest) | `type:web` | Rendering from the fixture, search, hash navigation, URL encoding of awkward names, the theme switcher, the error states, the build-output check, and the fixture against the schema | jsdom, `public/site-data.js` |
| `MySync.Testing` (`tests/mysync/testing/`) | `type:lib` | **Not a test project.** Shared fakes and builders: `InMemoryShareStore` (`ObjectUploads`, `ReadObject`), `InMemoryLocalSources` (push's disk: absolute paths, `.mysynctool` per folder, `Locked`, `BeforeOpen` to grow/shrink/remove a file, `ReadStream`), `TestPushRecordSerializer`, `SimplePatternIgnoreMatcher` (`FixedIgnoreRulesLoader.Compile`), `InMemoryLocalFolder` (also a simple `IFileScanner`; `ExecutableBitSupported = false` simulates Windows), `PathListIgnoreMatcher`, `ManifestBuilder` (real hashes + their bytes), `ManifestAssertions.ShouldMatchManifest`, `TempShareFolder`, `FixedDateTimeProvider`, `InstantTimeProvider`, `RecordingSyncProgressReporter`, `InMemoryShareStateStore`, `InMemoryFolderLock`, `RecordingConflictLog`, `FixedMachineIdentity`, `FixedIgnoreRulesLoader`, `TestContentHasher`, `FailingStream` (throws `IOException` partway), `InMemoryAliasStore` (with race, failure, re-point and listing hooks), `InMemorySiteAssetSource`, `InMemoryPublishTarget` (blobs + headers + an operation log, failure and race hooks), `InMemoryPublicationVerifier`, `TestSiteDataSerializer`, `NoReleases` / `NotInstalled`, `InMemoryLocalFolderFactory` (pull destinations), `ScriptedTerminal` (drives `mysync tui`: keys, waits for text on screen, actions in between, frames and enter/leave recorded), and the abstract contract suites `Contracts/ShareStoreContractTests`, `Contracts/AliasStoreContractTests` and `Contracts/LocalFolderContractTests` | |

### Conventions

- **Pipeline tests use two "machines".** `application-unit-tests/TestSupport/SyncWorld` gives one shared `InMemoryShareStore` and clock; each `Machine` has its own folder, state, lock and conflict log and resolves the real handlers (with decorators) from `AddApplication()`. Races and crashes are injected through `InMemoryShareStore.BeforeReplaceManifest` and `InMemoryLocalFolder.OnOperation`; broken streams through `InMemoryShareStore.DownloadStream` and `InMemoryLocalFolder.ReadStream`.
- **Handler tests never touch real storage or disk.** Use `InMemoryShareStore` / `InMemoryLocalFolder` for behaviour, and NSubstitute when a test is about one failing call.
- **Progress is asserted as a sequence**, not as counters. `RecordingSyncProgressReporter` writes each call down as a line, so a test pins the whole order. Nothing in `apps/mysync/Rendering` is unit-tested, so this is where the reporting contract is held.
- **One contract suite per abstraction, run against the fake and the real thing.** `LocalFolderContractTests` does for `ILocalFolder` what `ShareStoreContractTests` does for storage: it runs against `InMemoryLocalFolder` (as Unix and as Windows) in the application tests, and against the real folder in the infrastructure tests.
- **One contract suite, many stores.** `ShareStoreContractTests` (abstract, in `MySync.Testing`) pins ETag and conditional-write behaviour: `If-Match` mismatch → `PreconditionFailed`, `If-None-Match: *` on an existing blob → `AlreadyExists`, missing manifest → null, object idempotency. It runs against `InMemoryShareStore` (in application unit tests) **and** against real Azure and real S3 (in the storage integration tests), unchanged. If the fake and a provider disagree, the fake is wrong. What is genuinely provider-specific (whether identical bytes get a new ETag) is pinned in that provider's own tests, not the suite.
- Tests that touch the environment use their own variables and never read the developer's real `AZURE_STORAGE_CONNECTION_STRING`.

### Real test storage (no emulator)

The integration and end-to-end suites run against **real storage**, never an emulator: a test-only Azure storage account (plus one with static website hosting for publishing) and a test-only S3 bucket (plus a public publishing bucket), described in `docs/ci-storage.md`.

- **Configuration** (`MySync.Testing`): `AzureTestStorage` reads `MYSYNC_TEST_AZURE_CONNECTION_STRING` (account key), `MYSYNC_TEST_AZURE_PUBLISH_CONNECTION_STRING`, `MYSYNC_TEST_AZURE_PUBLISH_BASE_URL`; `S3TestStorage` reads `MYSYNC_TEST_S3_BUCKET`, `MYSYNC_TEST_S3_REGION`, `MYSYNC_TEST_S3_PUBLISH_BUCKET`, `MYSYNC_TEST_S3_PUBLISH_BASE_URL`, with credentials from the AWS chain. The developer's own `AZURE_STORAGE_CONNECTION_STRING` and `MYSYNC_S3_*` are never read.
- **Skip or fail:** a test whose storage isn't configured **skips** (`TestStorage.Unavailable`, an xUnit dynamic skip, from a constructor or a fixture property). With **`MYSYNC_TEST_REQUIRE_STORAGE=1`** (CI) it **fails** instead, so missing secrets can't pass as green. A project whose every test skipped exits 0 (`--ignore-exit-code 8` in its csproj).
- **Isolation:** Azure: a container per test (`AzureTestStorage.NewContainerName()`, `t-<8-hex run prefix>-<24 hex>`). S3: buckets can't be made per test (the documented policy has no `s3:CreateBucket`), so a key prefix per test (`S3TestStorage.NewKeyPrefix()`, `t-<run>/<24 hex>/`), passed to the stores as the tests-only `S3StorageOptions.KeyPrefix` (never bound from the environment; in production keys are exactly `ShareLayout`'s). The CLI-process round trip on S3 writes at the bucket root and removes its share and name itself.
- **Clean-up:** each assembly fixture (`AzureStorageFixture`, `S3StorageFixture`, the end-to-end `StorageFixture`) deletes this run's containers and everything under its S3 prefix, and the publications written since the run started. The test bucket also has a two-day expiry rule for runs that die.
- **CI:** `ci.yml`'s *Storage + end-to-end tests* job (and the release workflow's test job) maps repository secrets to these variables, runs `tools/ci/check-storage-secrets.sh` first (one error per missing secret), and runs one at a time (`concurrency: real-storage`). Pull requests from forks get no secrets and skip the job. The S3 user has exactly the permissions of `docs/aws.md`, so a call needing more fails a test.
- **Large-share tests** (`end-to-end-tests/LargeShareTests`: a 300 MB file, a 5,000-file share) skip unless `MYSYNC_TEST_LARGE=1`, which the manual and weekly `real-storage.yml` (*Large share tests*) sets for both providers.
- **What only real storage can prove** is the point: conditional-write races and their 409s, ETag behaviour, the anonymous reads of published files, credentials and permissions. What a substitute can prove (every error mapping, multipart edge cases, the preflight's logic) is in `MySync.Storage.S3.UnitTests`, run on every OS.

### What must be covered

**Domain:**
- Clone: `CloneTarget` (the folder for a name, an ID with and without a name, relative, parent-relative and absolute folders, the current folder, the first name in ordinal order).
- Push: `PushTarget` (form, `--host` sanitising, suffixes, invalid targets), `PushSourceLayout` (every landing rule and duplicate), `PushPlanner` (free folder, `TargetIsAFile`, and the preservation property over generated manifests), `PushSince` (every unit, offset / local / date-only times, `last` across hosts, suffixes and `--host`, refused input incl. future times), `ByteSize` (every unit and spelling, refused input), `PushFilter` (each filter, cutoff and size boundaries, exclude winning, the reason order), `PushPatterns`.
- `MergePlanner`: every row of the decision table, plus all combinations of `B/L/R ∈ {absent, h1, h2, dir}`, generated exhaustively.
- Compression: format 3 and its transitions, every `CompressionPolicy` branch and boundary, `ObjectEncoding` parsing; in the application tests (`Sync/CompressionTests`) a compressed share end to end, mixed raw/gzip shares, two machines racing with different settings (one object), a file changing while it's compressed, corrupt/truncated/oversized gzip, unknown encodings, publishing, and `OneDecoderTests` (only `ObjectCodec` opens objects).
- `LineDiff`: on random inputs the script turns A into B and is minimal (`|A|+|B|−2·LCS`, LCS by a plain DP in the test); `UnifiedDiff` hunks, headers and no-newline markers; `TextContent` detection.
- Paths: NFC/NFD (`NfcNormalizer` against the Unicode conformance file), `..` traversal, absolute paths, drive letters, Windows reserved names, case collisions.
- `MassDeleteGuard` thresholds at and around the boundaries.

**Application:**
- Create → clone → edit both sides → sync. Deletes, `--no-delete`, mass-delete guard.
- Conflicts: every conflict kind leaves local bytes, the remote manifest entry and the base entry untouched; it's recorded in `conflicts`; it's logged once (not again on an unchanged re-run); it clears via `L == R`, via revert to base, and via `resolve --keep local|remote`; `resolve` loses its race to a newer remote change and reports it.
- Crash between commit and apply (simulate by failing the local apply), then a clean re-run.
- Concurrent commit: precondition failure, then retry succeeds; retries exhausted → `CommitRetriesExhausted`.
- Validators for every command; decorators short-circuit on validation failure.
- Pull (`Sync/PullTests`): the whole share, a folder, a file, several paths, `--to` new and existing; bytes, mtimes and the executable bit; a compressed share; synced files and pushed folders, `--latest`, `--host`; the share's ignore rules not applied; a re-run finding everything there and an interrupted pull finishing; a differing file failing before any download and `--overwrite`; type clashes even with `--overwrite`; the same-share refusal (also from parents) and another share's warning; a missing and a corrupt object skipped; planted artifact names, case collisions and Windows names skipped; the share changing mid-pull; dry runs; **no store write, state, lock or log**; usage errors; a deleted share. Domain (`Sync/Pull/PullSelectionTests`): every landing rule, prefixes that aren't folders, duplicates, `--latest` with hosts, suffixes, non-host folders, no pushes, empty folders.
- Push (`Sync/PushTests`): the landing rules, suffixes, 10 concurrent pushes (sequential revisions, nothing lost), racing a sync, retries exhausted then a re-run uploading nothing, growing / replaced / short-read files, skips, excludes, the share's `.mysyncignore` warning and a clone skipping those files, the share's `.gitignore` and both together, `--create` (and losing its race), deleted shares, dry runs, the record, `SourceIsTheShare`, and no state, lock, log or staging touched. Filters (`Sync/PushFilterTests`): each alone and all four, `last` (first push everything, then only changes; other hosts; `--host`; `--create`), filtered files never opened nor skipped, the named-file warning, everything filtered (no commit), empty folders, an unfiltered report and record as before, dry runs, the share-ignore count on pushed files only, every invalid value in the validator. Compression (`Sync/PushCompressTests`): `--create --compress` (revision 1 and HEAD format 3 with gzip, objects and the record stored gzip), an existing share off (warning, raw) and on (no warning, gzip), with and without `--create`, losing the creation race to a share with and without compression, dry runs.
- No `Error` or log message produced in any test contains `AccountKey=`, `SharedAccessSignature=` or the test connection string.

**Infrastructure:**
- Hash cache: reuse, invalidation on size/mtime change, racy-mtime rule.
- State file: atomic write, corrupt file → `StateErrors.Corrupt` (never a new share), version-too-new → error.
- Ignore matching including built-in excludes; `.mysyncignore` itself is synced; every corpus case agrees with `git`. `GitIgnoreRulesLoaderTests`: either file alone, both as a union (a `!` in one doesn't re-include the other's), a subfolder `.gitignore` not read, an oversized one named; `DiskSyncTests`: both applied on create, clone and sync, both synced.
- Paths: an NFD name on disk is found by its NFC path; a path through a symlink is refused; a skipped directory hides its contents.
- Exec bit and mtime application on Unix (skipped on Windows runners).

**Real-storage suites (both providers):**
- The `IShareStore` and `IAliasStore` contract suites (`AzureShareStoreContractTests`, `S3ShareStoreContractTests`, …), plus Azure-only cases: multi-block uploads, a mismatched upload leaving nothing visible, metadata, stray blobs ignored, a mid-download failure as `IOException`, configuration errors, `Unauthorized` / `Unreachable`, and no key in any error or log line.
- Real round-trip of create / clone / two-way sync with two temp folders (`end-to-end-tests/SyncTests`, per provider, each machine the full DI graph via `Machine`).
- Two pipelines committing concurrently: both finish, nothing lost, revisions strictly sequential.
- A 17 MB file (several Azure blocks, two S3 parts) upload and download with hash verification; large shares in `real-storage.yml`.
- S3: the conditional-write answers (412, and 409 retried) as the contract's concurrency cases see them; multipart uploads and copies; the publishing preflight against the real public bucket.
- `gc` keeps objects referenced by kept history and within the grace period.

**CI (`ci.yml`):** a `site` job (Node from `.nvmrc`, `npm ci`, then `nx run-many -t lint,typecheck,test,build -p mysync-site`) runs first and uploads the build; every .NET job downloads it into `dist/apps/mysync-site` before building. Then build + unit + architecture tests (the S3 unit tests included) on `ubuntu-latest`, `windows-latest` and `macos-latest`, and the real-storage suites on `ubuntu-latest`.

---

## 15. Build, run, test

Prefer **Nx targets**. They wrap the `dotnet` commands and give caching and a uniform interface. Run from the repo root.

```bash
nx build mysync                                          # builds mysync-site first, then the CLI (and its dependencies)
nx run-many -t lint,typecheck,test,build -p mysync-site  # the publication page (typecheck is `tsc -b`: app, tests and build config)
npm run site                                             # the page with a mocked share, in your browser (npm run site:large: 100,000 files)
nx run-many -t test --projects=tag:type:unit-test        # all unit tests
nx run-many -t test --projects=tag:type:architecture-test
nx run-many -t test --projects=tag:type:integration-test,tag:type:e2e-test   # real-storage suites (docs/ci-storage.md; skip without it)
nx run-many -t test --all                                # everything
nx publish mysync --rid=linux-x64                        # local single-file publish (same flags as the release workflow)
```

Direct .NET commands (the solution is `mysync.slnx`; `global.json` selects Microsoft.Testing.Platform). **The CLI embeds the site build** (`dist/apps/mysync-site`): without it a **Release** build fails (`MYSYNC001`: run `nx build mysync`), and a **Debug** build warns (`MYSYNC002`) and embeds a placeholder page that `mysync publish` refuses. So run `nx build mysync-site` once before `dotnet build -c Release`.

> **`tsc -b`, not `tsc --noEmit -p …`**: `apps/mysync-site/tsconfig.json` is a solution-style config (`files: []` plus references to `tsconfig.app.json` and `tsconfig.spec.json`). `--noEmit -p tsconfig.app.json` checks only the app and skips the tests and build config; `tsc -b` checks every referenced project.

```bash
dotnet build                                                                     # whole solution, warnings = errors
dotnet test                                                                      # every test project
dotnet test --project tests/mysync/domain-unit-tests/MySync.Domain.UnitTests.csproj
dotnet run --project apps/mysync -- status

# Local single-file publish — mirror of the release flags (§16)
dotnet publish apps/mysync/MySync.Cli.csproj -c Release -r linux-x64 --self-contained \
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -p:InvariantGlobalization=true -p:DebugType=embedded -o out/linux-x64
```

Manual testing against your own test storage: set `AZURE_STORAGE_CONNECTION_STRING` (Azure) or `MYSYNC_PROVIDER=s3`, `MYSYNC_S3_BUCKET`, `MYSYNC_S3_REGION` and AWS credentials (S3), as `docs/azure.md` and `docs/aws.md` describe.

`global.json`:

```json
{
  "sdk": { "version": "10.0.100", "rollForward": "latestFeature" },
  "test": { "runner": "Microsoft.Testing.Platform" }
}
```

---

## 16. Releasing mysync

`.github/workflows/release-mysync.yml` builds and publishes `apps/mysync`. Pushing a tag matching `mysync-*` is the whole release process:

```bash
git tag mysync-1.2.3 && git push origin mysync-1.2.3   # or mysync-v1.2.3-beta.1
```

The project version lives in one place, `<Version>` in `Directory.Build.props` (every csproj inherits it; don't set it per project). Bump it in the commit you tag, so local builds report the same version.

The tag is the version. `mysync-` and an optional `v` are stripped; the rest must be `MAJOR.MINOR.PATCH` with an optional `-suffix`, and it's passed to `dotnet publish` as `-p:Version=` (so `mysync --version` matches the tag). A suffix marks the GitHub release as a prerelease. Run the workflow from the Actions tab with a version input to rehearse: it builds and uploads the archives as workflow artifacts but publishes nothing.

- **Seven targets, all cross-published from one Linux runner:** `linux-x64`, `linux-arm64`, `linux-musl-x64`, `osx-x64`, `osx-arm64`, `win-x64`, `win-arm64`. A self-contained, non-AOT publish doesn't need a matching host, and a Unix host keeps the executable bit inside the archives. Don't move the Unix targets onto a Windows runner.
- **Publish flags are release-packaging concerns and live in the workflow** (and the mirrored Nx `publish` target), not the csproj: `--self-contained` (no .NET runtime needed on the target machine), `PublishSingleFile` + `EnableCompressionInSingleFile` + `IncludeNativeLibrariesForSelfExtract` (one executable), `InvariantGlobalization=true` (drops the libicu dependency; without it the binary dies with an ICU error on a minimal server image. NFC doesn't depend on ICU because we implement it ourselves, §8), `DebugType=embedded` (stack traces keep line numbers, no separate `.pdb`).
- **Not trimmed**, deliberately: the configuration binder and Serilog resolve types by reflection, and the Azure SDK emits trim warnings. Trimming breaks these silently.
- Archives are `mysync-<version>-<rid>.tar.gz` (Unix) / `.zip` (Windows), plus a collected `SHA256SUMS.txt`.
- **The site is built once:** the `site` job runs `npm ci` and the site's lint/typecheck/test/build and uploads `dist/apps/mysync-site`; the test job and all seven builds download that same build, so every binary embeds identical assets.
- **Tests before release:** the release workflow's *Test* job runs every suite, the storage and end-to-end ones against the real test storage, with the same secrets as `ci.yml` (`docs/ci-storage.md`); a missing secret fails the release.
- **Smoke tests:** a step runs the `linux-x64` binary inside a stock `ubuntu:24.04` container and the `linux-musl-x64` binary inside `alpine`, asserts each prints the tagged version, runs the NFC-normalization self-check (§8), and runs the hidden `mysync debug site-assets`, which lists each embedded asset with its SHA-256 and exits `1` on the Debug placeholder. That's the self-containment claim tested rather than assumed. Keep it.
- Binaries are unsigned. Release notes tell macOS users about `xattr -d com.apple.quarantine ./mysync` and Windows users about SmartScreen. Signing is the next step if that becomes a problem.
- **Signing:** the repository secret **`MYSYNC_RELEASE_SIGNING_KEY`** (a PEM P-256 private key) is the release's identity. The *Release identity* job checks it first (fails before any build if the secret is missing or not P-256). Every build gets `-p:MySyncReleaseRepository=${{ github.repository }}` and `-p:MySyncReleasePublicKey=<its public half>`, which each job derives itself with `tools/release/public-key.sh` (and fails if it's empty, rather than silently trusting the built-in key). **Never pass the public key through a job output:** GitHub masks every line of a secret, a line of an EC private key's PEM can occur inside its public key's base64, and the output then arrives empty (this broke the first 0.5.0 release run). The *Publish release* job signs `SHA256SUMS.txt` (`openssl dgst -sha256 -sign`), checks the signature against the derived key, and publishes `SHA256SUMS.txt.sig` plus `mysync-release-signing.pub.pem`. In the upstream repository (`UPSTREAM`) the key must also equal `tools/release/mysync-release-signing.pub.pem` (= `ReleaseSignature.ProductionPublicKeyPem`, pinned by a test), so local builds trust the same key.
- **Forks** release their own mysync by adding their own `MYSYNC_RELEASE_SIGNING_KEY` secret and nothing else: their binaries update from the fork and trust the fork's key. Only a fork that hosts its own install scripts edits the two marked lines in each (repository and public key). `ReleaseSignatureTests` keeps the upstream scripts in step with the code. Never commit the private key. Rotating it means binaries built before the rotation can't verify later releases: publish one release signed with the old key whose binary trusts the new one.
- **`tools/release/update-e2e.sh <rid>`** (CI job *Self-update end to end*, Linux and macOS): publishes 9.9.8 and 9.9.9 trusting a throwaway key, and checks `update --check`, a refused tampered archive and wrong-key signature, the real update, and `install.sh` against the same release folder. `tools/release/install-ps1-check.ps1` checks `install.ps1`'s signature code on Windows PowerShell 5.1 and 7 against `tools/release/testdata` (test-only key; marked binary in `.gitattributes`, since a line-ending change would break the signatures).
- **Install scripts** (`install.sh` for macOS/Linux, `install.ps1` for Windows) are for the first install only, then `mysync update` takes over. Same checks as `update`: signature (the release key is inlined), checksum, `--version` and `self-check` before the binary is moved into place. They install into a user-writable folder by default (`~/.local/bin`, `%LOCALAPPDATA%\Programs\mysync`). `MYSYNC_INSTALL_SOURCE` / `MYSYNC_INSTALL_PUBLIC_KEY_FILE` exist for the end-to-end test.
- **Release notes** are the heredoc in the *Create the release* step: what mysync is, a link to the README at that tag, the download table, install and checksum steps per OS (Alpine needs `libstdc++ libgcc`), a quick start and the command list, then GitHub's generated change list. `VERSION`, `REPOSITORY` and `/TAG/` are substituted by `sed`; keep those placeholders unique. The README is the full user guide: keep its commands, options and exit codes in step with §10.

---

## 17. Adding a feature — end-to-end checklist

1. **Domain** (`libs/mysync/domain`): extend value objects, `MergePlanner` or `ShareState` as needed, and add factories to the relevant `*Errors` class.
2. **Application** (`libs/mysync/application/<Feature>/<UseCase>/`): the command or query, its `internal sealed` handler returning `Result`, a validator if it takes arguments, and a response DTO.
3. **Abstractions:** new external capability? Define it in `libs/mysync/application/Abstractions/`.
4. **Infrastructure / storage:** implement new abstractions in `libs/mysync/infrastructure` or the storage project, and register them in that project's `DependencyInjection.cs`. A new storage capability means updating the `IShareStore` contract suite and `InMemoryShareStore` too.
5. **CLI** (`apps/mysync/Commands/` + `Rendering/`): a thin command, its view, `--json` shape, and any new `ExitCodes` mapping.
6. **Tests:** unit tests at each layer you touched, contract tests if storage changed, and an end-to-end case if user-visible behaviour changed.
7. **Verify:** `dotnet build` clean, unit + architecture tests green on all OSes, real-storage suites green (CI).
8. **Docs:** update this file if a rule, layout, command, exit code or package changed.

---

## 18. Suggested implementation order

1. Workspace skeleton: Nx, `mysync.slnx`, `global.json`, `Directory.Build.props`, `Directory.Packages.props`, all empty projects with tags, architecture tests green. Publish a "hello" single-file binary for all seven RIDs and run the `InvariantGlobalization` + `Normalize` check from §8 inside the published Linux binary.
2. `SharedKernel` (`Result`, `Error`) and the Domain: value objects, `SharePath` rules, `Manifest`, `ShareState`, `ShareLayout`.
3. `MergePlanner` (pure) and its exhaustive tests. `MassDeleteGuard`.
4. Application abstractions, messaging and decorators. `InMemoryShareStore`, `InMemoryLocalFolder` and the `IShareStore` contract suite in `MySync.Testing`.
5. `SyncPipeline` + `RemoteCommitter`, then the Create, Clone and Run slices against the in-memory fakes.
6. Infrastructure: scanner with hash cache, hasher, state store + lock, ignore loader, conflict log, manifest serializer.
7. `MySync.Storage.Azure` + the storage integration tests against real test storage (contract suite first).
8. CLI host, commands, rendering, exit codes, safety guards. End-to-end tests.
9. `status`, `info`, `conflicts`, `resolve`, `gc`.
10. `ci.yml` and `release-mysync.yml`; README with install, env-var setup and the macOS/Windows notes.

---

## 19. Roadmap (not v1; don't build unless asked)

- S3-compatible services (R2, MinIO, …): only after verifying their conditional writes and error codes against the contract suites; today there is deliberately no endpoint setting.
- Compression: zstd (a new `ObjectEncoding`, a higher manifest format); recompressing or decompressing objects already stored; a per-run opt-out (e.g. `sync --no-compress` on a slow CPU). Manifests stay uncompressed.
- Share codes with an embedded, scoped, expiring SAS so recipients need no env credentials. Needs either one container per share or an ADLS Gen2 (hierarchical namespace) account for directory SAS. Keep `ShareLayout` flexible for this.
- `mysync restore --revision <n>` using history manifests.
- `watch` mode, client-side encryption, gzip manifests, chunk-level dedupe for large, slowly-changing files.
- `mysync tui`: a web page on the same `IFolderSession`; pull, push filters and pushing the whole folder from it; mouse support; a light theme; file watching.
- Pull: filters (`--include` / `--exclude`, size and time), `--revision <n>` (an older revision), a `mysync ls` to browse a share, removing local files that left the share, anything that makes a later pull incremental.
- Push: filters on `mysync sync`, `--min-size`, negated patterns, reading patterns from a file, applying a `.mysyncignore` to a push; choosing the target folder (`--to`), and overwriting (`--overwrite`).
- Publishing: `--expires <duration>` through blob lifecycle-management rules on `$web/p/<id>/`; `publish --replace <publishId>` (same URL, new snapshot); Azure Front Door / a custom domain in front of the static website (only `MYSYNC_PUBLISH_BASE_URL` changes).
