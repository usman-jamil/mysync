# CLAUDE.md — mysync

Guidance for working in this repository. This is an **Nx monorepo** that houses the **mysync** stack:

- **`apps/mysync`** — the CLI: a **.NET 10** console app (`MySync.Cli.csproj`, command name `mysync`) built with ConsoleAppFramework on `Microsoft.Extensions.Hosting`, rendering through Spectre.Console.
- **`libs/mysync/*`** — its **Clean Architecture** backend: domain, application (use cases + abstractions), infrastructure (local disk, state file, hashing), and one project per **storage provider** (Azure Blob today, S3 later).
- **`libs/shared/shared-kernel`** — dependency-free building blocks (`Result`, `Error`, `IDateTimeProvider`).
- **`tests/mysync/*`** — unit, integration (**Azurite**), end-to-end and architecture tests.

> Working name: **mysync** (executable `mysync`, local state file `.mysynctool`).
> A rename touches `AssemblyName` in `apps/mysync/MySync.Cli.csproj`, the file-name constants in `MySync.Domain.State.StateFileNames`, the release workflow's tag prefix and archive names (§16), and this file. Change them in the same commit.

The most important thing this file does is tell you **where a change belongs**. Decide which project owns it first (§2), then follow that project's rules.

---

## 1. What this is

`mysync` is an internal, cross-platform command-line tool for a small team. It moves folders between machines (laptops ↔ remote servers) through an **Azure Blob Storage container**. **No server or service of our own** sits behind it: all shared state lives in the blob container, and all per-folder state lives in a `.mysynctool` file inside the synced folder.

How it's used:

```text
# Machine A — in any folder
$ mysync sync
Created share 0b6f3c1e-8f0a-4c43-9d3e-5a1f2b7c9e10 (142 files, 38.2 MB)
Share it with:  mysync sync 0b6f3c1e-8f0a-4c43-9d3e-5a1f2b7c9e10

# Machine B — in an EMPTY folder
$ mysync sync 0b6f3c1e-8f0a-4c43-9d3e-5a1f2b7c9e10
Cloned share 0b6f3c1e… (142 files, 38.2 MB)

# Afterwards, on either machine, inside that folder
$ mysync sync        # two-way sync: upload local changes, download remote changes
```

### Original requirements (from the product owner, keep these true)

1. Simple CLI. **Never prompts for credentials.** They come from environment variables only.
2. **No backend dependency.** Only the storage provider (Azure Blob for now).
3. `sync` in a folder with no `.mysynctool` creates a **new share**, uploads the folder, writes `.mysynctool` and prints a share ID (UUID).
4. `sync <id>` clones a share into the current folder. **Allowed only in an empty folder.**
5. Later `sync` runs in a shared folder do a **two-way sync** (upload and download).
6. Change detection is **hash-based**, not timestamp-based.
7. **Content-addressed storage layout**: blob names are content hashes, not mirrored folder paths.
8. **All platforms**: Windows, Linux, macOS (x64 and arm64).
9. Built in **.NET**, published as a **single self-contained executable** with the runtime bundled.
10. Azure only for now, but the storage layer must be swappable (S3 etc. later).
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
├── package.json                  # Nx toolchain only (no runtime JS) + workspace scripts
├── mysync.slnx                   # .NET solution: every project under apps/, libs/, tests/
├── global.json                   # pins the .NET 10 SDK + selects Microsoft.Testing.Platform for dotnet test
├── Directory.Build.props         # shared MSBuild settings (nullable, analyzers, warnings-as-errors)
├── Directory.Packages.props      # Central Package Management — every NuGet version lives here
├── .editorconfig                 # formatting + analyzer severities
├── .gitattributes                # *.cs checked out as CRLF everywhere, so IDE0055 agrees on every CI OS
│
├── tools/
│   └── unicode/GenerateNfcData.cs  # file-based app: UCD → NFC tables + conformance data (§8)
│
├── apps/
│   └── mysync/                   # MySync.Cli.csproj — the executable + composition root   (Nx: "mysync")
│       ├── Program.cs            #   host, DI wiring, provider selection, command registration
│       ├── Commands/             #   <Feature>Commands.cs — thin: resolve handler, invoke, render
│       ├── Rendering/            #   everything the user sees (Spectre.Console)
│       ├── Hosting/              #   HostServiceProviderFactory (ConsoleAppFramework ↔ Microsoft.Extensions.Hosting, §4)
│       ├── GlobalOptions.cs      #   --verbose / --quiet / --json / --parallel
│       ├── StorageProvider.cs    #   MYSYNC_PROVIDER → which provider to register
│       └── ExitCodes.cs          #   Error code → process exit code (§10)
│
├── libs/                         # class libraries, one folder per stack
│   ├── mysync/                   # backend owned exclusively by apps/mysync
│   │   ├── domain/               # MySync.Domain.csproj               (Nx: "mysync-domain")
│   │   ├── application/          # MySync.Application.csproj          (Nx: "mysync-application")
│   │   ├── infrastructure/       # MySync.Infrastructure.csproj       (Nx: "mysync-infrastructure")
│   │   └── storage-azure/        # MySync.Storage.Azure.csproj        (Nx: "mysync-storage-azure")
│   │                             #   later: storage-s3/ MySync.Storage.S3.csproj
│   └── shared/
│       └── shared-kernel/        # SharedKernel.csproj — no dependencies (Nx: "shared-kernel")
│
├── tests/                        # test projects, mirroring libs/
│   └── mysync/
│       ├── testing/                          # MySync.Testing.csproj — shared fakes & builders (not a test project)
│       ├── domain-unit-tests/                # MySync.Domain.UnitTests.csproj
│       ├── application-unit-tests/           # MySync.Application.UnitTests.csproj
│       ├── infrastructure-unit-tests/        # MySync.Infrastructure.UnitTests.csproj
│       ├── storage-azure-integration-tests/  # MySync.Storage.Azure.IntegrationTests.csproj (Azurite)
│       ├── end-to-end-tests/                 # MySync.EndToEndTests.csproj (Azurite, full DI graph, two "machines")
│       └── architecture-tests/               # MySync.ArchitectureTests.csproj (dependency rules)
│
└── .github/workflows/
    ├── ci.yml                    # build + unit tests on ubuntu/windows/macos; Azurite suites on ubuntu
    └── release-mysync.yml        # tag-driven publish of every RID (§16)
```

> **Convention:** every .NET **class library** lives under `libs/mysync/` or `libs/shared/` (one folder per project). Only the executable host (`apps/mysync/MySync.Cli.csproj`) lives under `apps/`. `mysync.slnx` references all of them.
>
> `libs/mysync/` is **private to the mysync stack**. If another tool is added to this monorepo later, it gets its own `libs/<tool>/` and never references `libs/mysync/*`. `libs/shared/` is the only place a library several stacks may reference belongs.

Each project is an **Nx project** defined by its `project.json` (targets wrap `dotnet` via `nx:run-commands` unless the workspace adopts a .NET Nx plugin). Every project carries **tags**: `stack:mysync` or `stack:shared`, plus `type:lib`, `type:app`, `type:unit-test`, `type:integration-test`, `type:e2e-test` or `type:architecture-test`. CI and the commands in §15 select test suites by tag.

### Decision table — which project owns a change

| You are changing… | It belongs in… | Notes |
|---|---|---|
| Merge rules, manifest shape, path rules, conflict kinds, safety-guard thresholds | `libs/mysync/domain` | Pure logic. No I/O, no framework references. |
| A use case (`sync`, `clone`, `resolve`, `gc`, `status`, `info`) or its sequencing | `libs/mysync/application/<Feature>/<UseCase>/` | Handlers own the sequence, not the I/O. |
| A new external capability (disk, clock, storage, logging sink) | interface in `libs/mysync/application/Abstractions/`, implementation in infrastructure or a storage project | Dependency inversion, see §3. |
| Local disk, `.mysynctool` serialization, hashing, ignore matching, conflict log | `libs/mysync/infrastructure` | |
| How a share is stored in Azure (blob calls, ETags, error mapping) | `libs/mysync/storage-azure` | The only project that references `Azure.Storage.Blobs`. |
| A new storage provider | a **new** `libs/mysync/storage-<name>` | Implements `IShareStore`; registered by the composition root. |
| What the CLI looks like on screen | `apps/mysync/Rendering/` | Spectre.Console. No rendering below `apps/`. |
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
- `MySync.Storage.Azure` is the **only** project that references `Azure.*` packages.
- `MySync.Cli` is the composition root. It references Application, Infrastructure and the storage projects, and wires everything together at startup. Only it references Spectre.Console and ConsoleAppFramework.

These rules are enforced by `tests/mysync/architecture-tests` (NetArchTest). **If you add a reference that points outward, those tests fail by design. Fix the design, not the test.**

### What belongs in each project

| Project (path) | Responsibility | Contains |
|---|---|---|
| `SharedKernel` (`libs/shared/shared-kernel/SharedKernel.csproj`) | Building blocks | `Result` / `Result<T>`, `Error`, `ErrorType`, `ValidationError`, `IDateTimeProvider`. No dependencies. |
| `MySync.Domain` (`libs/mysync/domain/MySync.Domain.csproj`) | Sync rules | Value objects (`ShareId`, `SharePath`, `ContentHash`, `Revision`), `Manifest` / `ManifestEntry`, `ShareState`, `MergePlanner`, `SyncPlan` / `SyncAction`, `Conflict` / `ConflictKind`, `MassDeleteGuard`, `ShareLayout` (blob key scheme, provider-agnostic), `ShareState` + `StorageLocation` + `HashCacheEntry`, `CaseCollision`, `StateFileNames`, `NfcNormalizer` (our own Unicode NFC, §8), and **static `*Errors` classes** (`ShareErrors`, `SyncErrors`, `PathErrors`, `LocalFileErrors`, `StateErrors`, `StorageErrors`, `ConfigurationErrors`). No I/O. |
| `MySync.Application` (`libs/mysync/application/MySync.Application.csproj`) | Use cases | Command/query handlers, FluentValidation validators, **all abstractions** (`I...`), behaviors (decorators), response DTOs, the internal `SyncPipeline` that sequences the phases in §9. |
| `MySync.Infrastructure` (`libs/mysync/infrastructure/MySync.Infrastructure.csproj`) | Local machine | `ShareRoot` (the folder being synced), `LocalFolder` + `PathResolver` / `FileScanner`, `Sha256ContentHasher`, `JsonShareStateStore` + `FolderLock`, `FileConflictLog`, `GitIgnoreRulesLoader` + `GitIgnoreMatcher` (our own gitignore matcher), `ManifestSerializer` + `MySyncJsonContext` (source-generated JSON), `MachineIdentity`, `DateTimeProvider`, `DependencyInjection.AddInfrastructure()`. |
| `MySync.Storage.Azure` (`libs/mysync/storage-azure/MySync.Storage.Azure.csproj`) | Azure Blob provider | `AzureBlobShareStore : IShareStore`, `AzureStorageOptions`, `AzureErrorMapper` (`RequestFailedException` → `StorageErrors`), `ConnectionStringRedactor`, `DependencyInjection.AddAzureBlobStorage()`. |
| `MySync.Cli` (`apps/mysync/MySync.Cli.csproj`) | Presentation / composition root | `Program.cs`, `Commands/`, `Rendering/`, `ExitCodes.cs`. Selects the provider from `MYSYNC_PROVIDER`. |

### Where abstractions live (critical)

**All abstractions are defined in `MySync.Application/Abstractions/`, implemented in Infrastructure or a storage project.** Application code depends on interfaces only and never references a concrete type from an outer layer.

| Abstraction | Folder | Implemented by |
|---|---|---|
| `IShareStore` (+ `ManifestSnapshot`, `StoredObjectInfo`) | `Abstractions/Storage/` | `MySync.Storage.Azure` (later `.S3`) |
| `IManifestSerializer` | `Abstractions/Storage/` | Infrastructure (shared by every provider, so manifest JSON is identical everywhere) |
| `ILocalFolder`, `IFileScanner`, `LocalFile`, `ScannedFolder` | `Abstractions/FileSystem/` | Infrastructure |
| `IContentHasher` | `Abstractions/Hashing/` | Infrastructure |
| `IShareStateStore`, `IFolderLock` | `Abstractions/State/` | Infrastructure |
| `IConflictLog` | `Abstractions/Logging/` | Infrastructure |
| `IIgnoreRulesLoader`, `IIgnoreMatcher` | `Abstractions/Ignore/` | Infrastructure |
| `IMachineIdentity` | `Abstractions/Environment/` | Infrastructure |
| `ISyncProgressReporter`, `NullSyncProgressReporter` | `Abstractions/Progress/` | `apps/mysync/Rendering/SyncProgressDisplay` |

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
    Preview/        PreviewNewShareQuery(.cs|Handler|Validator), NewSharePreview       # counts + size, no hashing
    Create/         CreateShareCommand(.cs|Handler|Validator), CreateShareResponse
    Clone/          CloneShareCommand(.cs|Handler|Validator), CloneShareResponse
    GetInfo/        GetShareInfoQuery(.cs|Handler), ShareInfoResponse
  Sync/
    Run/            RunSyncCommand(.cs|Handler|Validator), SyncReport
    Plan/           GetSyncPlanQuery(.cs|Handler), SyncPlanResponse                    # `status`, `--dry-run`
    Pipeline/       SyncPipeline, RemoteCommitter (412 retry loop), ObjectTransfers,   # internal, shared by Run/Clone/Resolve
                    LocalApplier (phase 8), ConflictTracker, ProgressReportingStream
    SyncOptions     max parallelism (MYSYNC_PARALLELISM), commit attempts, back-off base
  Conflicts/
    List/           ListConflictsQuery(.cs|Handler), ConflictListResponse
    Resolve/        ResolveConflictsCommand(.cs|Handler|Validator), ResolveReport
  Maintenance/
    CollectGarbage/ CollectGarbageCommand(.cs|Handler|Validator), GarbageReport
  Diagnostics/
    SelfCheck/      RunSelfCheckQuery(.cs|Handler), SelfCheckResponse                  # `self-check`, release smoke test (§8, §16)
```

The matching command lives in `apps/mysync/Commands/<Feature>Commands.cs`.

### Cross-cutting concerns are behaviors (decorators)

Validation and logging are **not** written inside handlers. They're decorators in `Abstractions/Behaviors`, applied with Scrutor's `Decorate(...)` in `MySync.Application/DependencyInjection.cs`:

- `ValidationDecorator` runs every FluentValidation `IValidator<TCommand>` before the handler. On failure it returns `Result.Failure(ValidationError)` without calling the handler. Validators cover argument shape: share ID is a UUID, `--keep` is `local|remote`, `resolve` has paths or `--all` (not both), `--parallel` is 1–64, and so on.
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
- Register `AddApplication()`, `AddInfrastructure()`, then the provider chosen by `MYSYNC_PROVIDER` (`azure-blob` → `AddAzureBlobStorage()`). An unknown provider is a configuration error (exit `2`), reported when a command needs storage, not at startup. `AddInfrastructure()` takes the share root (`ShareRoot`) from the current directory unless one is registered first (tests register a temp folder).
- Commands are registered with `consoleApp.Add<T>()`. Global options (`--verbose`, `--quiet`, `--json`, `--parallel`) use ConsoleAppFramework's global-options support.
- **Don't use `builder.ToConsoleAppBuilder()`.** As soon as `ConfigureGlobalOptions` is called, ConsoleAppFramework builds its own (empty) service provider after parsing and replaces the host's, so no handler resolves. Instead `ConsoleApp.Create().ConfigureContainer(new HostServiceProviderFactory(builder))`: ConsoleAppFramework then builds the host itself, after global options are parsed, and the parsed `GlobalOptions` is registered into the same container via `ConfigureServices`.
- `--verbose` / `--quiet` set a Serilog `LoggingLevelSwitch` from inside the `ConfigureGlobalOptions` callback. Write that lambda's parameter type out (`(ref ConsoleApp.GlobalOptionsBuilder options) => …`): with the inferred C# 14 form `(ref options)`, the ConsoleAppFramework 5.7.13 generator fails with a NullReferenceException (CS8785).
- Ctrl+C cancels the token passed to every handler. The process exits `130`.

### `apps/mysync/Commands/`

| File | Commands |
|---|---|
| `SyncCommands.cs` | `sync [<shareId>]`, `status` |
| `ConflictCommands.cs` | `conflicts`, `resolve` |
| `ShareCommands.cs` | `info` |
| `MaintenanceCommands.cs` | `gc` |

- **Keep commands thin:** resolve the handler from a scope, invoke it, render the result, return the exit code.
- `sync` with no argument and no `.mysynctool` is the **create** flow. The command first runs `PreviewNewShareQuery` (file count and size, no hashing), asks for confirmation if needed (§9), then runs `CreateShareCommand`.
- **Confirmation lives in the command, never in a handler.** `--yes` never reaches the Application layer. A non-interactive stdin without `--yes` above the thresholds fails instead of assuming consent.
- **A command given all its arguments never prompts.** No credentials are ever prompted. The only prompt in the whole tool is the large-share `y/N`.

### `apps/mysync/Rendering/` — everything the user sees

| Piece | What it is |
|---|---|
| `ConsoleOutput` | The one way to write: `Fail` / `Warn` / `Success`, `Value` / `Muted` / `Label` (escaped), byte and count formatting, `IsInteractive`, `CanRedraw` |
| `SyncProgressDisplay` | `ISyncProgressReporter` for `sync`, `clone` and `resolve` (upload and download bars) |
| `SyncReportView` | End-of-run summary: transferred, deleted, **conflicts**, skipped (with reasons) |
| `PlanView` | `status` / `--dry-run` output |
| `ConflictView` | `conflicts` table (path, kind, local vs remote hash/size/mtime, remote `updatedBy` and revision) |
| `ShareInfoView` | `info` field list |
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
| `AZURE_STORAGE_CONNECTION_STRING` | yes (for storage commands) | Storage account connection string. `UseDevelopmentStorage=true` targets local Azurite. |
| `MYSYNC_CONTAINER` | no | Blob container name. Default `mysync`. |
| `MYSYNC_PROVIDER` | no | Default `azure-blob`. Reserved for future providers. |
| `MYSYNC_PARALLELISM` | no | Max concurrent transfers. Default `min(ProcessorCount * 2, 16)`. `--parallel` overrides it. |

- Options bind from environment variables into `AzureStorageOptions` / `SyncOptions`. They're **not validated at startup**, so `mysync --help` and `--version` work with nothing configured. A missing variable surfaces as `ConfigurationErrors.Missing(name)` → exit `2`, with a one-line message naming the variable.
- **Never prompt** for any credential or setting.
- **Never print, log or store the connection string or account key**, including in exceptions, `--verbose` output, `--json` output and `.mysynctool`. `ConnectionStringRedactor` (`AccountKey=***`, `SharedAccessSignature=***`) is applied to anything from the Azure SDK that might echo it.
- `.mysynctool` records the **provider, account name and container**, never secrets. If the current environment points at a different account or container than the one recorded, fail (`StateErrors.StorageMismatch`) rather than syncing against the wrong place.
- The container is created (`CreateIfNotExists`) only by the **create-share** flow.

---

## 6. Remote storage layout (content-addressed)

Everything for a share lives under one prefix in the container. Key building is `MySync.Domain.Storage.ShareLayout`, the one place blob names are composed:

```text
<container>/
└── shares/<shareId>/
    ├── manifest.json                       # HEAD: the current state of the share (single commit point)
    ├── manifests/<revision:D10>.json       # immutable history snapshots (best-effort, for recovery + GC safety)
    └── objects/<h0h1>/<sha256-hex>         # file contents, named by SHA-256 of the content
```

- Blob names come from `ShareLayout.For(shareId)` (`.Manifest`, `.ManifestHistory(rev)`, `.ObjectBlob(hash)`), and `TryParseObject` / `TryParseManifestHistory` accept only canonical names, so a stray blob is never taken for an object. `ShareLayoutTests` pins the exact strings: changing them breaks every existing share.
- `shareId`: lowercase UUID, `Guid.NewGuid().ToString("D")`. `ShareId.TryParse` accepts only the 36-character form (no braces, no whitespace, not the nil UUID).
- Object name: lowercase hex SHA-256 of the **uncompressed file bytes**. The first two hex characters form the fan-out folder (`objects/ab/abcdef…`).
- Objects are **immutable**. An object blob is never overwritten or modified. Upload with `If-None-Match: *`; a `409/412` because it already exists counts as success (same name, same content).
- **The store verifies every upload's SHA-256 and stores nothing on a mismatch** (`StorageErrors.HashMismatch`). A file that changed while it was being read must never become an object under the wrong name: because objects are immutable and an existing object is never re-uploaded, a wrong one would stay wrong. (Azure: stage the blocks while hashing, commit the block list only if the hash matches.) The pipeline treats a mismatch as "unstable", skips the path and retries it next run.
- Object blob metadata: `sha256=<hex>`, `size=<bytes>`. Content-Type `application/octet-stream`.
- Identical files in a share are stored once. Renames cost nothing (only the manifest changes).
- Dedupe is **per share**, not across shares. Shares stay independent, which keeps GC and deletion trivial.
- `ShareLayout` is provider-agnostic (an S3 provider uses the same keys). Keep it flexible enough to move to one container per share later (§19).

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
| `.mysynctool.log` | append-only, human-readable log of conflicts detected and resolved (`IConflictLog`): `2026-11-16T09:02:55Z CONFLICT modify/modify reports/q3.xlsx local=bb22… remote=cc33… (rev 18, ali@SERVER01)` and `… RESOLVED keep-local reports/q3.xlsx`. Rotate at 5 MB (keep one `.mysynctool.log.1`). No secrets |

### `.mysyncignore`

- gitignore syntax, read from the share root only (v1), via `IIgnoreRulesLoader`. UTF-8 (BOM fine), LF or CRLF, at most 1 MB.
- **It is synced**, so the whole team shares the same rules.
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
6. **Upload** objects needed by local-wins actions (parallel, skip if the object exists).
7. **Commit** the new manifest = remote manifest + local-wins changes applied (protocol §6). On precondition failure → back to phase 3.
8. **Apply remote-wins actions locally:**
   - Download to `.mysynctool.tmp/`, **verify SHA-256 while streaming** (mismatch → discard and error for that path), then move it into place (overwrite).
   - Before overwriting or deleting a local file, **re-check it still matches what the scan saw** (size + mtime). If it changed meanwhile, skip that path; it gets picked up next run.
   - Local deletes remove the file, then remove parent directories that became empty (unless the manifest has them as `dir` entries).
9. **Update state:** new `base` = committed manifest, **except** paths whose local apply failed or was skipped keep their *old* base entry, so the next run sees that change again. Write `.mysynctool` atomically. Release the lock.

**Rules the pipeline adds on top of the merge** (all tested in `application-unit-tests/Sync`):

- **Paths the sync can't see are frozen.** If the scan skipped a path (unstable, unreadable, symlink) or the local ignore rules match it, its local side is taken to be its base, and its action is deferred. A skipped *directory* freezes everything under it too: an unreadable folder must not look like "all its files were deleted here". A file that is absent from the scan because it is busy or ignored is never read as "deleted here", and nothing is downloaded over it. The manifest keeps its remote entry; a remote-only ignored entry is reported as `IgnoredLocally`.
- **Deferred means "keep the old base".** An upload the store rejects (`HashMismatch`, the file changed mid-upload), a file changed after the scan, a failed apply, a case-only collision on a case-insensitive filesystem, or a Windows-invalid name on Windows: the path is left out of this run (`SyncPlan.WithDeferred`), keeps its base entry, and is reported in `SyncReport.Skipped` with a `SkipCause`. The next run sees it again.
- **Phase 8 deletes first**, deepest path first, then downloads, directory creations and mode changes in parallel, so a file can become a directory (and back) in one run. A directory that isn't empty when it should be deleted is deferred, not forced.
- **A conflict's remote details** (`remote.revision`, `updatedBy`) come from the HEAD the plan was made against, not from this run's own commit. An unchanged conflict keeps them and its `firstDetectedUtc`.
- Upload progress counts an object the store already had as transferred, so the bar completes; `SyncReport.Uploaded` counts only bytes actually sent.

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

---

## 10. CLI surface

```text
mysync sync [<shareId>] [--dry-run] [--no-delete] [--allow-mass-delete] [--yes] [--force]
mysync status            # read-only: the plan sync would execute, plus conflicts/warnings
mysync info              # share id, account/container, base revision vs remote revision, file count, size, open conflicts
mysync conflicts         # list open conflicts with local vs remote hash/size/mtime/who
mysync resolve <path>... --keep local|remote [--dry-run]
mysync resolve --all     --keep local|remote [--dry-run]
mysync gc [--dry-run] [--keep-revisions 20] [--grace-hours 24]
mysync --version
mysync self-check       # runtime checks inside the shipped binary (Unicode normalization); exit 1 if any fail
```

Global options: `--verbose`, `--quiet`, `--json` (machine-readable result on stdout; logs go to stderr), `--parallel <n>`.

### `resolve` (the only way a conflict is decided)

Runs the normal pipeline (lock → scan → read remote → plan), but for the named conflicted paths it overrides row 4:

- `--keep local`: upload the local version (or record the local delete) and commit it with `If-Match`. On precondition failure, re-plan. If the path's remote side changed again in the meantime, it's still a conflict: report it and don't force it.
- `--keep remote`: download the remote version over the local file (or delete the local file if the remote side deleted it). Re-check the local file still matches the scanned hash before overwriting, as in phase 8.
- Sets the base entry for the path to the resolved state, removes it from `conflicts`, and writes a `RESOLVED` line to `.mysynctool.log`.
- A path that isn't currently in conflict → error for that path, exit `2`. `resolve` touches only the named conflicted paths. It doesn't run a full sync of other paths.

### `gc` (never automatic)

Deletes object blobs in the share that are **not referenced** by HEAD or by any of the last `--keep-revisions` history manifests, **and** whose `LastModified` is older than `--grace-hours`. The grace period protects objects uploaded by another client's sync that hasn't committed yet. Prune history manifests older than the kept window. Always print what will be (or was) deleted.

### Exit codes (`apps/mysync/ExitCodes.cs`)

Mapped from `Error.Code` (failures) or from the report (partial successes). Nothing below `apps/` knows about exit codes.

| Code | Meaning | Source |
|---|---|---|
| 0 | Success, nothing notable | |
| 1 | Unexpected error | unhandled exception |
| 2 | Usage or configuration error | `ValidationError`, `ConfigurationErrors.*`, `resolve` on a non-conflicted path |
| 3 | Completed, but one or more conflicts were dropped and logged (still open) | `SyncReport.Conflicts` non-empty (takes precedence over 9) |
| 4 | Folder precondition failed | `ShareErrors.FolderNotEmpty`, `AlreadyAShare`, `UnsafeRoot` |
| 5 | Remote concurrency retries exhausted | `SyncErrors.CommitRetriesExhausted` |
| 6 | Another mysync process holds the lock for this folder | `StateErrors.FolderLocked` |
| 7 | Safety guard tripped (mass delete) | `SyncErrors.MassDeleteBlocked` |
| 8 | Share not found / storage auth failure | `ShareErrors.NotFound`, `StorageErrors.Unauthorized` |
| 9 | Completed, but some paths were skipped (locked, unstable, invalid name) | `SyncReport.Skipped` non-empty |
| 130 | Cancelled (Ctrl+C) | `OperationCanceledException` from the caller's token |

### Output

- Default: concise progress (`↑ 12 files 4.1 MB  ↓ 3 files 220 KB  ✗ 0`) and a final summary listing conflicts and skipped paths with reasons.
- When the terminal can't redraw, no animated progress (see §4).
- Messages say what happened and what to do next. No stack traces unless `--verbose`.

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
    Task<Result> UploadObjectAsync(ShareId shareId, ContentHash hash, Stream content, long length, CancellationToken ct); // verifies SHA-256 (HashMismatch); existing = success
    Task<Result<Stream>> OpenObjectReadAsync(ShareId shareId, ContentHash hash, CancellationToken ct);      // NotFound
    IAsyncEnumerable<Result<StoredObjectInfo>> ListObjectsAsync(ShareId shareId, CancellationToken ct);     // canonical object names only
    Task<Result> DeleteObjectAsync(ShareId shareId, ContentHash hash, CancellationToken ct);                // missing = success
}

public sealed record ManifestSnapshot(Manifest Manifest, string ETag);
public sealed record StoredObjectInfo(ContentHash Hash, long Size, DateTimeOffset LastModifiedUtc);
```

- **Every method returns `Result` / `Result<T>`.** The provider translates its SDK's failures into `StorageErrors` (`NotFound`, `PreconditionFailed`, `AlreadyExists`, `Unauthorized`, `Unreachable`, `Timeout`). Application code never sees `Azure.RequestFailedException`. Only `OperationCanceledException` from the caller's own token propagates.
- Azure-specific retries use the SDK's built-in retry policy (exponential, ~5 tries). The pipeline's own retry is only for manifest precondition failures.
- `AzureBlobShareStore` gets `IManifestSerializer` injected. It never serializes manifests itself.
- `MySync.Testing` provides an `InMemoryShareStore` that enforces ETag semantics exactly like Azure (`If-Match`, `If-None-Match: *`). Most pipeline tests run against it. Its behaviour is pinned against Azurite by a shared contract-test suite (§14).
- Future S3 provider: S3 supports conditional writes (`If-None-Match` / `If-Match` on PutObject). Verify that against the target S3-compatible service before relying on it, keep the same commit protocol, and run the same contract-test suite against it.

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
| `Azure.Storage.Blobs` | `MySync.Storage.Azure` **only** | Blob provider |
| `SonarAnalyzer.CSharp` | all (via `Directory.Build.props`) | Analyzers |
| `xunit.v3` | tests | Test framework, on **Microsoft.Testing.Platform** |
| `Shouldly` | tests | Assertions |
| `NSubstitute` | tests | Substitutes for abstractions in handler tests |
| `xunit.v3.extensibility.core` | `MySync.Testing` | `[Fact]` and `TestContext` for the abstract contract suites, without making the library a test project |
| `Testcontainers.Azurite` | `MySync.Testing`, Azurite-backed suites | Starts the Azurite container. Keep ≥ 4.16: 4.13 pulls in an SSH.NET with known vulnerabilities (NU1903 fails the build) |
| `Microsoft.Extensions.DependencyInjection` | tests only | A concrete `ServiceCollection` for DI tests (the libraries reference only the abstractions) |
| `NetArchTest.Rules` | architecture tests | Dependency rules (§3) |

Don't add a package without a row here. Use the BCL (`IncrementalHash`, `System.Text.Json`, `File.SetUnixFileMode`) where it's enough. There is deliberately no gitignore package: neither candidate matched git (§7), so `.mysyncignore` matching is our own code, checked against git.

---

## 14. Testing

Every test project is xUnit v3 on the Microsoft Testing Platform with Shouldly, registered in `mysync.slnx`, and tagged in its `project.json` (§2).

| Project (path) | Tag | Tests | Fakes / backing |
|---|---|---|---|
| `MySync.Domain.UnitTests` (`tests/mysync/domain-unit-tests/`) | `type:unit-test` | `MergePlanner`, `SharePath` / `PathRules`, `MassDeleteGuard`, `ShareLayout`, value objects | none (pure) |
| `MySync.Application.UnitTests` (`tests/mysync/application-unit-tests/`) | `type:unit-test` | Handlers, validators, `SyncPipeline`, `RemoteCommitter`, decorators | `InMemoryShareStore`, `InMemoryLocalFolder`, NSubstitute for single-failure cases |
| `MySync.Infrastructure.UnitTests` (`tests/mysync/infrastructure-unit-tests/`) | `type:unit-test` | `LocalFolderContractTests` on disk, `LocalFolder`, `FileScanner`, `Sha256ContentHasher`, hash cache, `JsonShareStateStore`, `FolderLock`, `FileConflictLog` rotation, `GitIgnoreMatcher` (against `git`), `ManifestSerializer` (exact JSON pinned), and `DiskSyncTests`: the real pipeline over two temp folders with `InMemoryShareStore` | real temp directories (`TempShareFolder`), `git` |
| `MySync.Storage.Azure.IntegrationTests` (`tests/mysync/storage-azure-integration-tests/`) | `type:integration-test` | `AzureBlobShareStore` against **Azurite**, plus the `IShareStore` **contract suite** | Azurite |
| `MySync.EndToEndTests` (`tests/mysync/end-to-end-tests/`) | `type:e2e-test` | Full DI graph (Application + Infrastructure + Azure) with two temp folders as two "machines" | Azurite |
| `MySync.ArchitectureTests` (`tests/mysync/architecture-tests/`) | `type:architecture-test` | The dependency rules in §3 | none |
| `MySync.Testing` (`tests/mysync/testing/`) | `type:lib` | **Not a test project.** Shared fakes and builders: `InMemoryShareStore`, `InMemoryLocalFolder` (also a simple `IFileScanner`; `ExecutableBitSupported = false` simulates Windows), `PathListIgnoreMatcher`, `ManifestBuilder` (real hashes + their bytes), `ManifestAssertions.ShouldMatchManifest`, `TempShareFolder`, `FixedDateTimeProvider`, `InstantTimeProvider`, `RecordingSyncProgressReporter`, `InMemoryShareStateStore`, `InMemoryFolderLock`, `RecordingConflictLog`, `FixedMachineIdentity`, `FixedIgnoreRulesLoader`, `TestContentHasher`, and the abstract contract suites `Contracts/ShareStoreContractTests` and `Contracts/LocalFolderContractTests` | |

### Conventions

- **Pipeline tests use two "machines".** `application-unit-tests/TestSupport/SyncWorld` gives one shared `InMemoryShareStore` and clock; each `Machine` has its own folder, state, lock and conflict log and resolves the real handlers (with decorators) from `AddApplication()`. Races and crashes are injected through `InMemoryShareStore.BeforeReplaceManifest` and `InMemoryLocalFolder.OnOperation`.
- **Handler tests never touch real storage or disk.** Use `InMemoryShareStore` / `InMemoryLocalFolder` for behaviour, and NSubstitute when a test is about one failing call.
- **Progress is asserted as a sequence**, not as counters. `RecordingSyncProgressReporter` writes each call down as a line, so a test pins the whole order. Nothing in `apps/mysync/Rendering` is unit-tested, so this is where the reporting contract is held.
- **One contract suite per abstraction, run against the fake and the real thing.** `LocalFolderContractTests` does for `ILocalFolder` what `ShareStoreContractTests` does for storage: it runs against `InMemoryLocalFolder` (as Unix and as Windows) in the application tests, and against the real folder in the infrastructure tests.
- **One contract suite, many stores.** `ShareStoreContractTests` (abstract, in `MySync.Testing`) pins ETag and conditional-write behaviour: `If-Match` mismatch → `PreconditionFailed`, `If-None-Match: *` on an existing blob → `AlreadyExists`, missing manifest → null, object idempotency. It runs against `InMemoryShareStore` (in application unit tests) **and** against Azurite (in the storage integration tests). If the fake and Azurite disagree, the fake is wrong.
- Tests that touch the environment use their own variables and never read the developer's real `AZURE_STORAGE_CONNECTION_STRING`.

### Azurite (the official Azure Storage emulator)

Both Azurite-backed suites use `Testcontainers.Azurite` with the image `mcr.microsoft.com/azure-storage/azurite`, **pinned to a specific tag** in one place (`MySync.Testing/AzuriteImage.cs`).

- **One container per test assembly** (an xUnit v3 assembly fixture). Each test uses **its own blob container** (`t-<guid-without-dashes>`, lowercase, ≤ 63 chars), so tests are isolated and can run in parallel.
- Run the blob service only.
- The Azure SDK can send an `x-ms-version` newer than the pinned Azurite knows. **Chosen:** Azurite starts with `--skipApiVersionCheck` (`MySync.Testing/AzuriteEnvironment.cs`).
- `AzuriteEnvironment` (in `MySync.Testing`) starts the container or picks up the bring-your-own instance and hands out container names. Each Azurite suite wraps it in its own small `AzuriteFixture : IAsyncLifetime`, registered with `[assembly: AssemblyFixture(...)]`.
- **Bring-your-own Azurite:** if `MYSYNC_TEST_AZURITE_CONNECTION_STRING` is set, the fixture uses that running instance instead of starting a container (for machines without Docker, or faster local loops).
- Docker is required otherwise. CI runs the Azurite suites on the Ubuntu runner only.
- Azurite isn't Azure. Anything only real Azure can prove (throughput, real auth failures) can go in an optional, manually triggered workflow against a real account. It isn't part of v1.

### What must be covered

**Domain:**
- `MergePlanner`: every row of the decision table, plus all combinations of `B/L/R ∈ {absent, h1, h2, dir}`, generated exhaustively.
- Paths: NFC/NFD (`NfcNormalizer` against the Unicode conformance file), `..` traversal, absolute paths, drive letters, Windows reserved names, case collisions.
- `MassDeleteGuard` thresholds at and around the boundaries.

**Application:**
- Create → clone → edit both sides → sync. Deletes, `--no-delete`, mass-delete guard.
- Conflicts: every conflict kind leaves local bytes, the remote manifest entry and the base entry untouched; it's recorded in `conflicts`; it's logged once (not again on an unchanged re-run); it clears via `L == R`, via revert to base, and via `resolve --keep local|remote`; `resolve` loses its race to a newer remote change and reports it.
- Crash between commit and apply (simulate by failing the local apply), then a clean re-run.
- Concurrent commit: precondition failure, then retry succeeds; retries exhausted → `CommitRetriesExhausted`.
- Validators for every command; decorators short-circuit on validation failure.
- No `Error` or log message produced in any test contains `AccountKey=`, `SharedAccessSignature=` or the test connection string.

**Infrastructure:**
- Hash cache: reuse, invalidation on size/mtime change, racy-mtime rule.
- State file: atomic write, corrupt file → `StateErrors.Corrupt` (never a new share), version-too-new → error.
- Ignore matching including built-in excludes; `.mysyncignore` itself is synced; every corpus case agrees with `git`.
- Paths: an NFD name on disk is found by its NFC path; a path through a symlink is refused; a skipped directory hides its contents.
- Exec bit and mtime application on Unix (skipped on Windows runners).

**Azurite suites:**
- The `IShareStore` contract suite.
- Real round-trip of create / clone / two-way sync with two temp folders.
- Two pipelines committing concurrently: both finish, nothing lost, revisions strictly sequential.
- Large file (e.g. 300 MB generated) upload and download with hash verification.
- `gc` keeps objects referenced by kept history and within the grace period.

**CI (`ci.yml`):** build + unit + architecture tests on `ubuntu-latest`, `windows-latest` and `macos-latest`. Azurite suites on `ubuntu-latest`.

---

## 15. Build, run, test

Prefer **Nx targets**. They wrap the `dotnet` commands and give caching and a uniform interface. Run from the repo root.

```bash
nx build mysync                                          # dotnet build the CLI (and its dependencies)
nx run-many -t test --projects=tag:type:unit-test        # all unit tests
nx run-many -t test --projects=tag:type:architecture-test
nx run-many -t test --projects=tag:type:integration-test,tag:type:e2e-test   # Azurite suites (needs Docker or BYO Azurite)
nx run-many -t test --all                                # everything
nx publish mysync --rid=linux-x64                        # local single-file publish (same flags as the release workflow)
```

Direct .NET commands (the solution is `mysync.slnx`; `global.json` selects Microsoft.Testing.Platform):

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

Manual testing against a local Azurite:

```bash
docker run -d -p 10000:10000 mcr.microsoft.com/azure-storage/azurite azurite-blob --blobHost 0.0.0.0 --skipApiVersionCheck
export AZURE_STORAGE_CONNECTION_STRING="UseDevelopmentStorage=true"
```

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
- **Smoke tests:** a step runs the `linux-x64` binary inside a stock `ubuntu:24.04` container and the `linux-musl-x64` binary inside `alpine`, asserts each prints the tagged version, and runs the NFC-normalization self-check (§8). That's the self-containment claim tested rather than assumed. Keep it.
- Binaries are unsigned. Release notes tell macOS users about `xattr -d com.apple.quarantine ./mysync` and Windows users about SmartScreen. Signing is the next step if that becomes a problem.

---

## 17. Adding a feature — end-to-end checklist

1. **Domain** (`libs/mysync/domain`): extend value objects, `MergePlanner` or `ShareState` as needed, and add factories to the relevant `*Errors` class.
2. **Application** (`libs/mysync/application/<Feature>/<UseCase>/`): the command or query, its `internal sealed` handler returning `Result`, a validator if it takes arguments, and a response DTO.
3. **Abstractions:** new external capability? Define it in `libs/mysync/application/Abstractions/`.
4. **Infrastructure / storage:** implement new abstractions in `libs/mysync/infrastructure` or the storage project, and register them in that project's `DependencyInjection.cs`. A new storage capability means updating the `IShareStore` contract suite and `InMemoryShareStore` too.
5. **CLI** (`apps/mysync/Commands/` + `Rendering/`): a thin command, its view, `--json` shape, and any new `ExitCodes` mapping.
6. **Tests:** unit tests at each layer you touched, contract tests if storage changed, and an end-to-end case if user-visible behaviour changed.
7. **Verify:** `dotnet build` clean, unit + architecture tests green on all OSes, Azurite suites green.
8. **Docs:** update this file if a rule, layout, command, exit code or package changed.

---

## 18. Suggested implementation order

1. Workspace skeleton: Nx, `mysync.slnx`, `global.json`, `Directory.Build.props`, `Directory.Packages.props`, all empty projects with tags, architecture tests green. Publish a "hello" single-file binary for all seven RIDs and run the `InvariantGlobalization` + `Normalize` check from §8 inside the published Linux binary.
2. `SharedKernel` (`Result`, `Error`) and the Domain: value objects, `SharePath` rules, `Manifest`, `ShareState`, `ShareLayout`.
3. `MergePlanner` (pure) and its exhaustive tests. `MassDeleteGuard`.
4. Application abstractions, messaging and decorators. `InMemoryShareStore`, `InMemoryLocalFolder` and the `IShareStore` contract suite in `MySync.Testing`.
5. `SyncPipeline` + `RemoteCommitter`, then the Create, Clone and Run slices against the in-memory fakes.
6. Infrastructure: scanner with hash cache, hasher, state store + lock, ignore loader, conflict log, manifest serializer.
7. `MySync.Storage.Azure` + the Azurite storage integration tests (contract suite first).
8. CLI host, commands, rendering, exit codes, safety guards. End-to-end tests.
9. `status`, `info`, `conflicts`, `resolve`, `gc`.
10. `ci.yml` and `release-mysync.yml`; README with install, env-var setup and the macOS/Windows notes.

---

## 19. Roadmap (not v1; don't build unless asked)

- S3 / S3-compatible provider (`libs/mysync/storage-s3`, `MySync.Storage.S3`), passing the same contract suite.
- Share codes with an embedded, scoped, expiring SAS so recipients need no env credentials. Needs either one container per share or an ADLS Gen2 (hierarchical namespace) account for directory SAS. Keep `ShareLayout` flexible for this.
- `mysync update` self-update from GitHub releases, reusing the archive naming and `SHA256SUMS.txt` from §16 (keep that naming in one domain class when it lands).
- `mysync restore --revision <n>` using history manifests.
- `mysync destroy <shareId>` (with confirmation).
- `watch` mode, client-side encryption, gzip manifests, chunk-level dedupe for large, slowly-changing files.
