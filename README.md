# mysync

Sync folders between machines through an Azure Blob Storage container. There is no server: shared
state lives in the container, and each folder's own state lives in a `.mysynctool` file inside it.

```text
mysync sync            # in a folder: create a new share (prints its ID), or two-way sync an existing one
mysync sync <shareId>  # in an empty folder: clone a share
```

> Status: workspace skeleton. Only `mysync self-check` is implemented so far. See `CLAUDE.md` §18
> for the implementation order.

## Configure

Environment variables only. mysync never prompts for credentials.

| Variable | Required | Default |
| --- | --- | --- |
| `AZURE_STORAGE_CONNECTION_STRING` | yes | `UseDevelopmentStorage=true` targets local Azurite |
| `MYSYNC_CONTAINER` | no | `mysync` |
| `MYSYNC_PROVIDER` | no | `azure-blob` |
| `MYSYNC_PARALLELISM` | no | `min(cores * 2, 16)` |

## Develop

Requires the .NET 10 SDK, Node (for Nx; see `.nvmrc`) and Docker for the Azurite suites.

```bash
npm install
npx nx build mysync
npx nx run-many -t test --projects=tag:type:unit-test,tag:type:architecture-test
npx nx run-many -t test --projects=tag:type:integration-test,tag:type:e2e-test   # Docker, or set MYSYNC_TEST_AZURITE_CONNECTION_STRING
dotnet run --project apps/mysync -- self-check --verbose
npx nx publish mysync --rid=osx-arm64          # single-file binary in out/osx-arm64
```

Releases are cut by pushing a `mysync-<version>` tag (see `CLAUDE.md` §16).
