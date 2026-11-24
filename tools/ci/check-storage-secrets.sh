#!/bin/sh
# Fails with one clear line per missing repository secret before anything is built, so a run
# against real test storage never "passes" by skipping (docs/ci-storage.md says how to set them up).
# Reads the MYSYNC_TEST_* / AWS_* variables the workflow maps the secrets to; prints no values.
set -eu

missing=0
check() { # <variable> <secret>
  eval "value=\${$1:-}"
  if [ -z "$value" ]; then
    echo "::error::Repository secret $2 is not set (it becomes $1). See docs/ci-storage.md."
    missing=1
  fi
}

check MYSYNC_TEST_AZURE_CONNECTION_STRING MYSYNC_REAL_AZURE_CONNECTION_STRING
check MYSYNC_TEST_AZURE_PUBLISH_CONNECTION_STRING MYSYNC_REAL_AZURE_PUBLISH_CONNECTION_STRING
check MYSYNC_TEST_AZURE_PUBLISH_BASE_URL MYSYNC_REAL_AZURE_PUBLISH_BASE_URL
check MYSYNC_TEST_S3_BUCKET MYSYNC_REAL_S3_BUCKET
check MYSYNC_TEST_S3_REGION MYSYNC_REAL_S3_REGION
check MYSYNC_TEST_S3_PUBLISH_BUCKET MYSYNC_REAL_S3_PUBLISH_BUCKET
check MYSYNC_TEST_S3_PUBLISH_BASE_URL MYSYNC_REAL_S3_PUBLISH_BASE_URL
check AWS_ACCESS_KEY_ID MYSYNC_REAL_AWS_ACCESS_KEY_ID
check AWS_SECRET_ACCESS_KEY MYSYNC_REAL_AWS_SECRET_ACCESS_KEY

exit "$missing"
