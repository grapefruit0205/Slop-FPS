#!/usr/bin/env bash
# Package the lobby Lambda, upload it to an artifacts bucket and deploy infra/backend.yaml.
#
#   ./deploy-backend.sh [--yes]
#
# FleetAliasId is resolved from the alias named ALIAS_NAME (created by deploy-server.sh).
# If the web stack exists, its CloudFront URL is appended to the CORS origins automatically.
set -euo pipefail
# shellcheck source=lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

usage() {
  cat <<USAGE
Usage: $0 [--yes] [--account-id ID] [--profile P] [--region R]
Writes the API base URL to deploy/out/api_url.txt
USAGE
}

parse_args "$@"
finish_flags
load_config

SRC_DIR="$SERVER_DIR/backend/src"
[[ -f "$SRC_DIR/index.mjs" ]] || die "backend sources not found in $SRC_DIR"

confirm_account "Deploy stack $BACKEND_STACK (Lambda + HTTP API + IAM role) and artifacts bucket"

ALIAS_ID="$(find_alias_id)"
[[ -n "$ALIAS_ID" ]] || die "GameLift alias '$ALIAS_NAME' not found - run deploy-server.sh first"
info "Fleet alias: $ALIAS_ID"

# ---- artifacts bucket (created once, private) --------------------------------------
BUCKET="$(artifact_bucket_name)"
if aws s3api head-bucket --bucket "$BUCKET" 2>/dev/null; then
  info "Artifacts bucket exists: $BUCKET"
else
  info "Creating artifacts bucket $BUCKET"
  if [[ "$AWS_REGION" == "us-east-1" ]]; then
    aws s3api create-bucket --bucket "$BUCKET" >/dev/null
  else
    aws s3api create-bucket --bucket "$BUCKET" \
      --create-bucket-configuration "LocationConstraint=$AWS_REGION" >/dev/null
  fi
  aws s3api put-public-access-block --bucket "$BUCKET" --public-access-block-configuration \
    BlockPublicAcls=true,IgnorePublicAcls=true,BlockPublicPolicy=true,RestrictPublicBuckets=true
fi

# ---- package: zip of src/ only (the nodejs22.x runtime ships AWS SDK v3) --------------
HASH="$(cd "$SRC_DIR" && find . -type f -print0 | LC_ALL=C sort -z | xargs -0 sha256sum | sha256sum | cut -c1-16)"
ZIP="$BUILD_DIR/lobby-$HASH.zip"
if [[ ! -f "$ZIP" ]]; then
  info "Packaging $ZIP"
  if command -v zip >/dev/null; then
    (cd "$SRC_DIR" && zip -qrX "$ZIP" .)
  else
    python3 - "$SRC_DIR" "$ZIP" <<'PY'
import os, sys, zipfile
src, out = sys.argv[1:3]
with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
    for root, _, files in os.walk(src):
        for f in sorted(files):
            p = os.path.join(root, f)
            z.write(p, os.path.relpath(p, src))
PY
  fi
fi
KEY="lobby/lobby-$HASH.zip"
aws s3 cp "$ZIP" "s3://$BUCKET/$KEY" --only-show-errors

# ---- CORS origins: config + web stack URL ---------------------------------------------
ORIGINS="$ALLOWED_ORIGINS"
if stack_exists "$WEB_STACK"; then
  SITE_URL="$(stack_output "$WEB_STACK" SiteUrl)"
  if [[ -n "$SITE_URL" && ",$ORIGINS," != *",$SITE_URL,"* ]]; then ORIGINS="$ORIGINS,$SITE_URL"; fi
fi
info "CORS origins: $ORIGINS"

PARAMS=(
  "ProjectName=$PROJECT"
  "FleetAliasId=$ALIAS_ID"
  "AllowedOrigins=$ORIGINS"
  "MaxPlayers=$MAX_PLAYERS"
  "CodeBucket=$BUCKET"
  "CodeKey=$KEY"
)
[[ -n "${LOCATION:-}" ]] && PARAMS+=("Location=$LOCATION")

info "Deploying stack $BACKEND_STACK"
aws cloudformation deploy \
  --stack-name "$BACKEND_STACK" \
  --template-file "$INFRA_DIR/backend.yaml" \
  --capabilities CAPABILITY_IAM \
  --no-fail-on-empty-changeset \
  --tags "Project=$PROJECT" \
  --parameter-overrides "${PARAMS[@]}"

API_URL="$(stack_output "$BACKEND_STACK" ApiUrl)"
[[ -n "$API_URL" ]] || die "stack deployed but ApiUrl output is missing"
echo "$API_URL" >"$OUT_DIR/api_url.txt"
echo
info "ApiUrl: $API_URL   (saved to $OUT_DIR/api_url.txt)"
echo "Try: curl -s $API_URL/rooms"
