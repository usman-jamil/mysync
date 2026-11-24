# Test storage for CI

mysync's storage and end-to-end tests run against **real storage**: a test-only Azure storage account and a test-only S3 bucket, each with a publishing side. There is no emulator. CI (`.github/workflows/ci.yml`, job *Storage + end-to-end tests*), the release workflow, and the weekly *Large share tests* workflow read the settings below from repository secrets.

Use storage that holds **nothing but test data**. The tests create and delete containers and keys in it, and publish (briefly) to its public side.

## Azure

These are the same accounts the old *Real Azure tests* workflow used. If those secrets exist, nothing changes.

1. A storage account for shares (follow [azure.md](azure.md), steps 1–3). The tests need its **account-key** connection string: the wrong-key test edits the key.
2. A second account with static website hosting on, index document `index.html` ([azure.md](azure.md), step 2).

| Secret | Value |
|---|---|
| `MYSYNC_REAL_AZURE_CONNECTION_STRING` | the shares account's account-key connection string (`az storage account show-connection-string … --key key2`) |
| `MYSYNC_REAL_AZURE_PUBLISH_CONNECTION_STRING` | the publishing account's account-key connection string |
| `MYSYNC_REAL_AZURE_PUBLISH_BASE_URL` | its static website address, e.g. `https://<account>.z13.web.core.windows.net` |

## AWS S3

Follow [aws.md](aws.md) to create a private bucket, a public publishing bucket and an IAM user, then adjust these for the tests:

1. **Two buckets**, e.g. `mysync-ci-shares` and `mysync-ci-publish`, in one region, set up exactly as the guide describes. The tests check the documented setup: the publishing bucket's bucket policy, Block Public Access settings as shown, and **versioning off** on the shares bucket.
2. **An expiry rule on the shares bucket**, so keys a cancelled run left behind don't pile up. Each run deletes what it wrote (everything under `t-<run>/`, plus its own shares). Add this rule to the guide's `lifecycle.json`, next to the incomplete-upload rule:

   ```json
   {
     "ID": "expire-test-data",
     "Status": "Enabled",
     "Filter": {},
     "Expiration": { "Days": 2 }
   }
   ```

3. **An IAM user for CI** with the guide's `mysync-policy.json` and nothing more. The tests run with exactly those permissions, so a missing one shows up as a failing test, not as a surprise for users. Create one access key for it:

   ```bash
   aws iam create-user --user-name mysync-ci
   aws iam put-user-policy --user-name mysync-ci --policy-name mysync-buckets --policy-document file://mysync-policy.json
   aws iam create-access-key --user-name mysync-ci
   ```

| Secret | Value |
|---|---|
| `MYSYNC_REAL_AWS_ACCESS_KEY_ID` | the CI user's `AccessKeyId` |
| `MYSYNC_REAL_AWS_SECRET_ACCESS_KEY` | its `SecretAccessKey` |
| `MYSYNC_REAL_S3_BUCKET` | the shares bucket, e.g. `mysync-ci-shares` |
| `MYSYNC_REAL_S3_REGION` | its region, e.g. `eu-west-1` (the publishing bucket must be in the same region) |
| `MYSYNC_REAL_S3_PUBLISH_BUCKET` | the publishing bucket, e.g. `mysync-ci-publish` |
| `MYSYNC_REAL_S3_PUBLISH_BASE_URL` | its HTTPS address, `https://<bucket>.s3.<region>.amazonaws.com` |

Bucket names and URLs aren't secret. They're kept as secrets anyway, so all the settings live in one place.

Rotate the key by creating a second one, updating the secret, and deleting the old one (`aws iam delete-access-key`).

## Adding them

GitHub → the repository → **Settings → Secrets and variables → Actions → New repository secret**, once per row above. Or with the GitHub CLI:

```bash
gh secret set MYSYNC_REAL_S3_BUCKET --body mysync-ci-shares
gh secret set MYSYNC_REAL_AWS_SECRET_ACCESS_KEY   # prompts for the value, so it stays out of your shell history
```

Every job that uses them first runs `tools/ci/check-storage-secrets.sh`, which names each missing secret and fails. The tests also run with `MYSYNC_TEST_REQUIRE_STORAGE=1`, so missing storage fails them instead of skipping them. Pull requests from forks get no secrets, and the storage job doesn't run for them.

## Running the suites yourself

Set the same values under their test names (the workflows map the secrets to these), then run the suites:

```bash
export MYSYNC_TEST_AZURE_CONNECTION_STRING='…'          # + MYSYNC_TEST_AZURE_PUBLISH_CONNECTION_STRING, MYSYNC_TEST_AZURE_PUBLISH_BASE_URL
export MYSYNC_TEST_S3_BUCKET=mysync-ci-shares MYSYNC_TEST_S3_REGION=eu-west-1
export MYSYNC_TEST_S3_PUBLISH_BUCKET=mysync-ci-publish MYSYNC_TEST_S3_PUBLISH_BASE_URL=https://mysync-ci-publish.s3.eu-west-1.amazonaws.com
export AWS_PROFILE=mysync-ci                             # or AWS_ACCESS_KEY_ID / AWS_SECRET_ACCESS_KEY

dotnet test --project tests/mysync/storage-azure-integration-tests/MySync.Storage.Azure.IntegrationTests.csproj
dotnet test --project tests/mysync/storage-s3-integration-tests/MySync.Storage.S3.IntegrationTests.csproj
dotnet test --project tests/mysync/end-to-end-tests/MySync.EndToEndTests.csproj
```

Without them, the tests that need storage skip, and everything else (the unit suites, and the parts of these suites that don't touch storage) still runs. The tests never read your own `AZURE_STORAGE_CONNECTION_STRING` or `MYSYNC_S3_BUCKET`.
