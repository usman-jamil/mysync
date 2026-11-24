# Contributing to mysync

Thanks for your interest! Bug reports, ideas, docs fixes and code are all welcome. This guide gets
you from a clone to a pull request. [`CLAUDE.md`](CLAUDE.md) is the full design reference: where
each kind of change belongs, the sync algorithm, the storage layout, and the rules behind them.

## Reporting a bug or asking for something

Open an [issue](https://github.com/usman-jamil/mysync/issues). For a bug, please include:

- `mysync --version` and your OS (and processor, e.g. Apple silicon or x64);
- the command you ran, and what happened versus what you expected;
- the output with `--verbose`. It never contains your connection string, but do check before
  posting: remove share IDs, file names or anything else you'd rather keep private.

**Security issues:** please don't open a public issue. Use GitHub's
[private vulnerability reporting](https://github.com/usman-jamil/mysync/security/advisories/new) instead.

For a larger change, open an issue first to talk it through. A design agreed early saves rework.

## Setting up

You need:

- the **.NET 10 SDK** (the exact version is pinned in `global.json`);
- **Node.js**, the version in `.nvmrc`, for Nx and the publication page;
- optionally, **test storage** (a test-only Azure account and S3 bucket) for the integration and
  end-to-end tests: [docs/ci-storage.md](docs/ci-storage.md). Without it those tests skip;
- **git**, which the ignore-rule tests compare against.

```bash
git clone https://github.com/usman-jamil/mysync.git
cd mysync
npm ci
npx nx build mysync          # builds the publication page, then the CLI
```

`nx build mysync` builds the web page first because the CLI embeds it. If you use `dotnet build`
directly, run `npx nx build mysync-site` once before it, or a Release build stops with a message
telling you to.

Try it against your own test storage ([docs/azure.md](docs/azure.md) or [docs/aws.md](docs/aws.md)):

```bash
export AZURE_STORAGE_CONNECTION_STRING='…'      # or MYSYNC_PROVIDER=s3, MYSYNC_S3_BUCKET, MYSYNC_S3_REGION and AWS credentials
dotnet run --project apps/mysync -- sync --name try-it
```

To look at the publication page with a mocked share: `npm run site`.

## Running the tests

```bash
npx nx run-many -t test --projects=tag:type:unit-test,tag:type:architecture-test   # fast, no storage
npx nx run-many -t lint,typecheck,test,build -p mysync-site                        # the web page
npx nx run-many -t test --projects=tag:type:integration-test,tag:type:e2e-test     # real test storage
npx nx run-many -t test --all                                                      # everything
```

CI runs the .NET tests on Linux, Windows and macOS, so path handling and the like must work on all
three. The storage suites run against real test storage, never an emulator: CI sets it up from
repository secrets, and [docs/ci-storage.md](docs/ci-storage.md) shows the variables to run them
yourself. Without them, the tests that need storage skip.

## Where a change goes

mysync follows Clean Architecture: dependencies only point inward, and architecture tests fail the
build if one points the wrong way. In short:

| You're changing… | It goes in… |
| --- | --- |
| Sync rules, manifests, paths, conflicts, publishing rules (pure logic) | `libs/mysync/domain` |
| A command's sequence (sync, clone, resolve, publish…) | `libs/mysync/application/<Feature>/<UseCase>/` |
| A new external capability | an interface in `libs/mysync/application/Abstractions/`, implemented outward |
| Local disk, state file, hashing, ignore rules | `libs/mysync/infrastructure` |
| How things are stored in Azure | `libs/mysync/storage-azure` (the only library that uses the Azure SDK) |
| A new storage provider (e.g. S3) | a new `libs/mysync/storage-<name>` that passes the shared contract tests |
| What the CLI prints, its options and exit codes | `apps/mysync` (`Commands/`, `Rendering/`) |
| The publication web page | `apps/mysync-site` (see its README) |

`CLAUDE.md` §2 and §3 have the full version, and §17 is a checklist for adding a feature.

## Code conventions

- **The build has no warnings.** Warnings are errors, with the full analyzer set (including
  SonarAnalyzer). Fix them; please don't suppress them without a good reason.
- **Expected failures are results, not exceptions.** Handlers return `Result` / `Result<T>`, with
  errors from the domain's `*Errors` classes.
- **Secrets never leak.** No connection string, account key or SAS token in any message, log,
  `--json` output or file. Tests check this; keep it that way.
- **Every file name is untrusted.** Escape it before it reaches the terminal, never build HTML
  from it, and quote it in any generated script.
- **C#:** file-scoped namespaces, primary constructors, `sealed` and `internal` by default, `async`
  with a `CancellationToken` all the way, source-generated JSON. `.cs` files use CRLF line endings
  on every OS (`.gitattributes` handles it).
- **The web page:** `npx nx lint mysync-site` must pass with no warnings; no external requests, and
  no `dangerouslySetInnerHTML`.
- **New packages** need a reason. NuGet versions live in `Directory.Packages.props` (and the table
  in `CLAUDE.md` §13), npm versions in the root `package.json`.

## Tests come with the change

- A bug fix comes with a test that fails without it.
- A new behaviour is tested at each layer it touches. The in-memory fakes in `tests/mysync/testing`
  (`InMemoryShareStore`, `InMemoryLocalFolder`, `InMemoryPublishTarget`, …) make most tests fast
  and need no storage.
- A storage change updates the shared contract suite, which runs against the fake and against real
  Azure and S3.
- A change you can see from the command line gets an end-to-end test (`tests/mysync/end-to-end-tests`).

## Pull requests

1. Fork, and branch from `main`.
2. Keep the pull request to one change. Small ones are easier to review and land faster.
3. Run the build and the tests above. If your change affects commands, options, exit codes or
   configuration, update `README.md` and `CLAUDE.md` in the same pull request.
4. Write commit messages that say **what changed and why**, in plain sentences, with a short first line.
5. Open the pull request with a description of the change and how you tested it. CI must pass.

Releases are cut by the maintainer from tags; contributors don't need to bump versions.

## License

By contributing, you agree that your contributions are licensed under the project's
[MIT License](LICENSE).
