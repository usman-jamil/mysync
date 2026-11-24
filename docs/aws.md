# Setting up AWS S3 for mysync

> [!NOTE]
> S3 support is coming in the next mysync release. Until then mysync works with [Azure Blob Storage](azure.md) only; the `MYSYNC_PROVIDER=s3` settings below are not recognised yet.

mysync needs one private bucket for shares. Publishing needs a second bucket that the public can read. These steps create both from scratch, plus an IAM user whose keys can reach those two buckets and nothing else.

**Before you start**

- Install the [AWS CLI v2](https://docs.aws.amazon.com/cli/latest/userguide/getting-started-install.html) and sign in with an account that may create buckets and IAM users (`aws configure`, or `aws sso login`).
- Pick two bucket names. Bucket names are unique across all of AWS, 3–63 characters, lowercase letters, digits and dashes. **Don't use dots**: they break HTTPS on S3 addresses.
- Pick a region close to your machines, for example `us-east-1`.

The commands below are the same in bash and PowerShell. Replace the three values wherever they appear:

| Placeholder | Example |
|---|---|
| `mysynccli` | your private bucket |
| `mysynccli-publish` | your publishing bucket |
| `us-east-1` | your region |

### 1. The private bucket (shares)

Create it:

```bash
# In us-east-1:
aws s3api create-bucket --bucket mysynccli --region us-east-1

# In any other region, the region is given twice:
aws s3api create-bucket --bucket mysynccli --region eu-west-1 --create-bucket-configuration LocationConstraint=eu-west-1
```

Keep it private. New buckets already block public access; this makes it explicit:

```bash
aws s3api put-public-access-block --bucket mysynccli --public-access-block-configuration BlockPublicAcls=true,IgnorePublicAcls=true,BlockPublicPolicy=true,RestrictPublicBuckets=true
```

Clean up interrupted large uploads after a day. Without this rule, S3 keeps charging for the unfinished pieces. Save this as `lifecycle.json`:

```json
{
  "Rules": [
    {
      "ID": "abort-incomplete-uploads",
      "Status": "Enabled",
      "Filter": {},
      "AbortIncompleteMultipartUpload": { "DaysAfterInitiation": 1 }
    }
  ]
}
```

```bash
aws s3api put-bucket-lifecycle-configuration --bucket mysynccli --lifecycle-configuration file://lifecycle.json
```

Leave **versioning off** (the default). With versioning on, files removed by `mysync delete` stay recoverable as old versions until you remove those too.

### 2. The publishing bucket (only if you use `mysync publish`)

Create it the same way:

```bash
aws s3api create-bucket --bucket mysynccli-publish --region us-east-1
# other regions: add  --create-bucket-configuration LocationConstraint=<region>
```

Allow a public-read policy on this bucket only (ACLs stay blocked):

```bash
aws s3api put-public-access-block --bucket mysynccli-publish --public-access-block-configuration BlockPublicAcls=true,IgnorePublicAcls=true,BlockPublicPolicy=false,RestrictPublicBuckets=false
```

Let anyone read published files, and nothing else. Save this as `publish-policy.json`:

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Sid": "PublicReadPublished",
      "Effect": "Allow",
      "Principal": "*",
      "Action": "s3:GetObject",
      "Resource": [
        "arn:aws:s3:::mysynccli-publish/p/*",
        "arn:aws:s3:::mysynccli-publish/robots.txt"
      ]
    }
  ]
}
```

```bash
aws s3api put-bucket-policy --bucket mysynccli-publish --policy file://publish-policy.json
aws s3api put-bucket-lifecycle-configuration --bucket mysynccli-publish --lifecycle-configuration file://lifecycle.json
```

- **Never grant `s3:ListBucket` to the public.** A listing would show every publication's ID, and those IDs are what keep the links private. `mysync publish` checks this and refuses a bucket that can be listed.
- **If `put-bucket-policy` fails with "public policies are blocked",** your AWS account blocks public buckets account-wide. Check with:

  ```bash
  aws s3control get-public-access-block --account-id <your 12-digit account id>
  ```

  Either ask an administrator to allow it, or keep the bucket private and put Amazon CloudFront in front of it. mysync only needs the public address (step 4).

The bucket's public address is:

```text
https://mysynccli-publish.s3.us-east-1.amazonaws.com
```

### 3. An IAM user and access keys

Give mysync its own user, limited to these two buckets. Don't use your root account's keys.

Save this as `mysync-policy.json` (leave out the two `mysynccli-publish` lines if you don't publish):

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Sid": "Buckets",
      "Effect": "Allow",
      "Action": [
        "s3:ListBucket",
        "s3:GetBucketLocation",
        "s3:GetBucketVersioning",
        "s3:ListBucketVersions",
        "s3:ListBucketMultipartUploads"
      ],
      "Resource": [
        "arn:aws:s3:::mysynccli",
        "arn:aws:s3:::mysynccli-publish"
      ]
    },
    {
      "Sid": "Objects",
      "Effect": "Allow",
      "Action": [
        "s3:GetObject",
        "s3:PutObject",
        "s3:DeleteObject",
        "s3:AbortMultipartUpload",
        "s3:ListMultipartUploadParts"
      ],
      "Resource": [
        "arn:aws:s3:::mysynccli/*",
        "arn:aws:s3:::mysynccli-publish/*"
      ]
    }
  ]
}
```

```bash
aws iam create-user --user-name mysync-cli
aws iam put-user-policy --user-name mysync-cli --policy-name mysync-buckets --policy-document file://mysync-policy.json
aws iam create-access-key --user-name mysync-cli
```

The last command prints `AccessKeyId` and `SecretAccessKey`. **The secret is shown only once**, so store it somewhere safe. Give each person or server its own key where you can, so one can be revoked without affecting the others:

```bash
aws iam list-access-keys --user-name mysync-cli
aws iam delete-access-key --user-name mysync-cli --access-key-id AKIA...
```

Alternatives that avoid long-lived keys:

- **On your own machine:** an AWS profile (`aws configure --profile mysync`, then set `AWS_PROFILE=mysync`) or AWS single sign-on.
- **On EC2 servers:** attach an IAM role that has the policy above. The server then needs no keys at all.

### 4. Configure mysync

mysync never asks for credentials. It reads them the standard AWS way (environment variables, a profile, single sign-on, or an instance role).

```bash
# macOS / Linux (e.g. in ~/.zshrc or ~/.bashrc)
export AWS_ACCESS_KEY_ID='AKIA...'
export AWS_SECRET_ACCESS_KEY='...'
export MYSYNC_PROVIDER='s3'
export MYSYNC_S3_BUCKET='mysynccli'
export MYSYNC_S3_REGION='us-east-1'

# only for mysync publish
export MYSYNC_PUBLISH_S3_BUCKET='mysynccli-publish'
export MYSYNC_PUBLISH_BASE_URL='https://mysynccli-publish.s3.us-east-1.amazonaws.com'
```

```powershell
# Windows PowerShell, this session only
$env:AWS_ACCESS_KEY_ID = 'AKIA...'
$env:AWS_SECRET_ACCESS_KEY = '...'
$env:MYSYNC_PROVIDER = 's3'
$env:MYSYNC_S3_BUCKET = 'mysynccli'
$env:MYSYNC_S3_REGION = 'us-east-1'

# only for mysync publish
$env:MYSYNC_PUBLISH_S3_BUCKET = 'mysynccli-publish'
$env:MYSYNC_PUBLISH_BASE_URL = 'https://mysynccli-publish.s3.us-east-1.amazonaws.com'

# to keep a value for future sessions:
[Environment]::SetEnvironmentVariable('MYSYNC_PROVIDER', 's3', 'User')
```

Published links end in `/index.html` on S3, because this address doesn't serve index pages.

### 5. Check the setup

With the `mysync-cli` keys set in your environment:

```bash
# Private bucket: write, read back, delete
echo hello > mysync-test.txt
aws s3 cp mysync-test.txt s3://mysynccli/mysync-test.txt
aws s3 cp s3://mysynccli/mysync-test.txt -
aws s3 rm s3://mysynccli/mysync-test.txt

# Publishing bucket: a file under p/ must be readable by anyone, and the bucket must not be listable
aws s3 cp mysync-test.txt s3://mysynccli-publish/p/mysync-test.txt
curl -i https://mysynccli-publish.s3.us-east-1.amazonaws.com/p/mysync-test.txt      # expect 200 and "hello"
curl -i "https://mysynccli-publish.s3.us-east-1.amazonaws.com/?list-type=2"          # expect 403 AccessDenied
aws s3 rm s3://mysynccli-publish/p/mysync-test.txt
```

Then:

```bash
mysync sync --name first-share        # in a folder with a few files
mysync shares
```

### Removing everything

```bash
aws s3 rb s3://mysynccli --force                 # deletes the bucket and everything in it
aws s3 rb s3://mysynccli-publish --force
aws iam list-access-keys --user-name mysync-cli  # delete each key shown:
aws iam delete-access-key --user-name mysync-cli --access-key-id AKIA...
aws iam delete-user-policy --user-name mysync-cli --policy-name mysync-buckets
aws iam delete-user --user-name mysync-cli
```
