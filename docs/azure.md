# Setting up Azure Blob Storage for mysync

mysync needs one private storage account for shares. Publishing needs a second account with static website hosting. These steps create both from scratch with the [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli), and give mysync a credential that reaches blob data in those accounts and nothing else.

**Before you start**

- Install the Azure CLI and sign in: `az login`. If you have several subscriptions, pick one: `az account set --subscription "<name or id>"`.
- Pick two storage account names. They are unique across all of Azure: 3–24 characters, lowercase letters and digits only.
- Pick a region close to your machines, for example `westeurope`.

The commands are the same in bash and PowerShell unless shown twice. Replace these values wherever they appear:

| Placeholder | Example |
|---|---|
| `mysync-rg` | a resource group for both accounts |
| `mysyncshares` | your private account (shares) |
| `mysyncpublish` | your publishing account |
| `westeurope` | your region |
| `mysync` | the blob container for shares (mysync's default) |

**How access works on Azure.** mysync signs in with a connection string. A connection string that holds an **account key** gives full control of that storage account, so:

- Use **accounts that hold nothing but mysync data**. A leaked credential then reaches only that data.
- Give machines a **SAS connection string** where you can (step 3, option B). It is limited to blob data, expires, and can be revoked without touching the admin key.

### 1. The resource group and the private account (shares)

```bash
az group create --name mysync-rg --location westeurope

az storage account create --name mysyncshares --resource-group mysync-rg --location westeurope --kind StorageV2 --sku Standard_LRS --min-tls-version TLS1_2 --https-only true --allow-blob-public-access false
```

- `--allow-blob-public-access false` means no container in this account can ever be made public.
- `--https-only true` and `--min-tls-version TLS1_2` refuse unencrypted and outdated connections.

Create the container for shares:

```bash
az storage container create --name mysync --account-name mysyncshares --auth-mode key
```

Check that **soft delete and versioning are off**. With either on, files removed by `mysync delete` stay recoverable until the retention period ends:

```bash
az storage account blob-service-properties show --account-name mysyncshares --resource-group mysync-rg --query "{softDelete:deleteRetentionPolicy.enabled, versioning:isVersioningEnabled}"

# if either is true:
az storage account blob-service-properties update --account-name mysyncshares --resource-group mysync-rg --enable-delete-retention false --enable-versioning false
```

Don't add a lifecycle rule that deletes blobs by age to this account: mysync keeps a file's stored copy for as long as any revision uses it, and such a rule would delete files that shares still need.

### 2. The publishing account (only if you use `mysync publish`)

```bash
az storage account create --name mysyncpublish --resource-group mysync-rg --location westeurope --kind StorageV2 --sku Standard_LRS --min-tls-version TLS1_2 --https-only true --allow-blob-public-access false
```

Turn on static website hosting, with exactly these document names (mysync checks them):

```bash
az storage blob service-properties update --account-name mysyncpublish --static-website --index-document index.html --404-document 404.html --auth-mode key
```

- This creates the `$web` container. **Only what is in `$web` is served publicly**, read-only, through the website address. Nobody can list it.
- `--allow-blob-public-access false` stays on: the static website doesn't need public containers, and every other container in the account stays private.

Get the website's public address:

```bash
az storage account show --name mysyncpublish --resource-group mysync-rg --query primaryEndpoints.web --output tsv
# https://mysyncpublish.z13.web.core.windows.net/
```

### 3. Credentials

Each account has two keys, `key1` and `key2`. Keep **`key1` for administrators**, and sign everything you hand out with **`key2`**. Renewing `key2` then revokes every credential that was handed out, and the admin key keeps working.

#### Option A — an account-key connection string (simplest)

```bash
az storage account show-connection-string --name mysyncshares --resource-group mysync-rg --key key2 --query connectionString --output tsv
az storage account show-connection-string --name mysyncpublish --resource-group mysync-rg --key key2 --query connectionString --output tsv
```

Each one gives full control of its account. Because the accounts hold only mysync data, that is the limit of what it can reach.

#### Option B — a SAS connection string for shares (least privilege)

A shared access signature (SAS) limits the credential to **blob data**, with only the permissions mysync uses, over HTTPS, until a date you choose. It can't change account settings, read the account keys, or touch queues, tables or file shares.

Create one signed with `key2`:

```bash
# bash
KEY2=$(az storage account keys list --account-name mysyncshares --resource-group mysync-rg --query "[1].value" --output tsv)
az storage account generate-sas --account-name mysyncshares --account-key "$KEY2" --services b --resource-types sco --permissions acdlrw --https-only --expiry 2027-06-30T00:00:00Z --output tsv
```

```powershell
# PowerShell
$key2 = az storage account keys list --account-name mysyncshares --resource-group mysync-rg --query "[1].value" --output tsv
az storage account generate-sas --account-name mysyncshares --account-key $key2 --services b --resource-types sco --permissions acdlrw --https-only --expiry 2027-06-30T00:00:00Z --output tsv
```

- `--services b`: the blob service only.
- `--resource-types sco`: the service, containers and blobs. mysync needs all three (it creates the container on the first share).
- `--permissions acdlrw`: add, create, delete, list, read, write.
- `--expiry`: after this date it stops working. Create a new one before then.
- Add `--ip <address or range>` to limit a server's SAS to that server's public address.

The command prints a token that starts with `se=` or `sv=`. The connection string is:

```text
BlobEndpoint=https://mysyncshares.blob.core.windows.net/;SharedAccessSignature=<the token>
```

Things to know about option B:

- **Publishing from a SAS-based share copies files through your machine** instead of inside Azure, and `mysync publish` says so. With an account-key connection string the copy happens inside Azure.
- **Use the account-key connection string (option A) for the publishing account.** mysync needs it there to check the static website settings.
- Create one SAS per person or server where you can, with its own expiry, so you know what each one is.

#### Rotating and revoking

```bash
# Revokes every SAS and connection string signed with key2:
az storage account keys renew --account-name mysyncshares --resource-group mysync-rg --key key2
az storage account keys renew --account-name mysyncpublish --resource-group mysync-rg --key key2
```

Then hand out new credentials (option A or B again). A single SAS can't be revoked on its own; renewing the key it was signed with is the way.

### 4. Configure mysync

mysync never asks for credentials. It reads them from environment variables only.

```bash
# macOS / Linux (e.g. in ~/.zshrc or ~/.bashrc)
export AZURE_STORAGE_CONNECTION_STRING='<the shares connection string from step 3>'
export MYSYNC_CONTAINER='mysync'            # optional: this is the default

# only for mysync publish
export MYSYNC_PUBLISH_CONNECTION_STRING='<the publishing connection string from step 3, option A>'
export MYSYNC_PUBLISH_BASE_URL='https://mysyncpublish.z13.web.core.windows.net'
```

```powershell
# Windows PowerShell, this session only
$env:AZURE_STORAGE_CONNECTION_STRING = '<the shares connection string from step 3>'
$env:MYSYNC_CONTAINER = 'mysync'

# only for mysync publish
$env:MYSYNC_PUBLISH_CONNECTION_STRING = '<the publishing connection string from step 3, option A>'
$env:MYSYNC_PUBLISH_BASE_URL = 'https://mysyncpublish.z13.web.core.windows.net'

# to keep a value for future sessions:
[Environment]::SetEnvironmentVariable('MYSYNC_CONTAINER', 'mysync', 'User')
```

Use single quotes around connection strings: they contain `;` and `=`. `MYSYNC_PUBLISH_BASE_URL` is the address from step 2 without the trailing `/`.

Connection strings are secrets. Keep them out of scripts you commit.

### 5. Check the setup

With the variables from step 4 set:

```bash
# Private account: write, read back, delete
echo hello > mysync-test.txt
az storage blob upload --connection-string "$AZURE_STORAGE_CONNECTION_STRING" --container-name mysync --name mysync-test.txt --file mysync-test.txt
az storage blob download --connection-string "$AZURE_STORAGE_CONNECTION_STRING" --container-name mysync --name mysync-test.txt --file mysync-test-back.txt
az storage blob delete --connection-string "$AZURE_STORAGE_CONNECTION_STRING" --container-name mysync --name mysync-test.txt

# It must NOT be readable without credentials (expect an error such as 409 PublicAccessNotPermitted, not the file):
curl -i https://mysyncshares.blob.core.windows.net/mysync/mysync-test.txt

# Publishing account: a file under p/ must be readable by anyone through the website address
az storage blob upload --connection-string "$MYSYNC_PUBLISH_CONNECTION_STRING" --container-name '$web' --name p/mysync-test.txt --file mysync-test.txt --content-type text/plain
curl -i https://mysyncpublish.z13.web.core.windows.net/p/mysync-test.txt      # expect 200 and "hello"
az storage blob delete --connection-string "$MYSYNC_PUBLISH_CONNECTION_STRING" --container-name '$web' --name p/mysync-test.txt
```

In PowerShell, use `$env:AZURE_STORAGE_CONNECTION_STRING` and `$env:MYSYNC_PUBLISH_CONNECTION_STRING` in place of the `$…` variables. Keep `'$web'` in single quotes in both shells.

Then:

```bash
mysync sync --name first-share        # in a folder with a few files
mysync shares
mysync publish --title "Test"         # prints a link; open it, then: mysync unpublish <id>
```

### Removing everything

```bash
# One account at a time:
az storage account delete --name mysyncshares --resource-group mysync-rg --yes
az storage account delete --name mysyncpublish --resource-group mysync-rg --yes

# Or the whole resource group, if it holds nothing else:
az group delete --name mysync-rg --yes
```

To see what exists first:

```bash
az storage account list --resource-group mysync-rg --query "[].name" --output table
az storage container list --account-name mysyncshares --auth-mode key --query "[].name" --output table
```
