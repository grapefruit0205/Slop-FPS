#!/usr/bin/env bash
# Delete everything these scripts created, EXCEPT the budget stack and uploaded GameLift builds:
#   backend stack, web stack (bucket is emptied first), GameLift alias, all fleets of this
#   project's builds. Optionally the Lambda artifacts bucket (--delete-artifacts).
#
#   ./teardown.sh [--delete-artifacts] [--yes --account-id <id>]
#
# Always requires typing the account id (or --yes together with --account-id).
set -euo pipefail
# shellcheck source=lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

usage() {
  cat <<USAGE
Usage: $0 [--delete-artifacts] [--yes --account-id ID] [--profile P] [--region R]
USAGE
}

parse_args "$@"
DEL_ART=0
take_flag --delete-artifacts && DEL_ART=1
finish_flags
load_config

# ---- inventory ------------------------------------------------------------------------
ALIAS_ID="$(find_alias_id)"
FLEETS=()
if [[ -n "$ALIAS_ID" ]]; then
  f="$(alias_fleet_id "$ALIAS_ID")"; [[ -n "$f" ]] && FLEETS+=("$f")
fi
for b in $(aws gamelift list-builds --query "Builds[?Name=='${PROJECT}-server'].BuildId" --output text | none_to_empty); do
  for f in $(aws gamelift list-fleets --build-id "$b" --query 'FleetIds' --output text | none_to_empty); do
    [[ " ${FLEETS[*]:-} " == *" $f "* ]] || FLEETS+=("$f")
  done
done
HAVE_BACKEND=0; stack_exists "$BACKEND_STACK" && HAVE_BACKEND=1
HAVE_WEB=0;     stack_exists "$WEB_STACK" && HAVE_WEB=1
ART_BUCKET=""

echo "Will DELETE in region $AWS_REGION:"
((HAVE_BACKEND)) && echo "  - CloudFormation stack $BACKEND_STACK (Lambda, HTTP API, role, log groups)"
((HAVE_WEB))     && echo "  - CloudFormation stack $WEB_STACK (CloudFront, and ALL objects in its S3 bucket)"
[[ -n "$ALIAS_ID" ]] && echo "  - GameLift alias $ALIAS_NAME ($ALIAS_ID)"
for f in "${FLEETS[@]:-}"; do [[ -n "$f" ]] && echo "  - GameLift fleet $f (all game sessions on it are terminated)"; done
echo "Will KEEP: budget stack $BUDGET_STACK, GameLift builds (free to store)."

confirm_account "TEARDOWN - delete the resources listed above" strict

# ---- delete ---------------------------------------------------------------------------
if ((HAVE_BACKEND)); then
  info "Deleting stack $BACKEND_STACK"
  aws cloudformation delete-stack --stack-name "$BACKEND_STACK"
  aws cloudformation wait stack-delete-complete --stack-name "$BACKEND_STACK"
fi
if ((HAVE_WEB)); then
  BUCKET="$(stack_output "$WEB_STACK" BucketName)"
  if [[ -n "$BUCKET" ]]; then
    info "Emptying s3://$BUCKET"
    aws s3 rm "s3://$BUCKET" --recursive --only-show-errors
  fi
  info "Deleting stack $WEB_STACK (CloudFront deletion can take several minutes)"
  aws cloudformation delete-stack --stack-name "$WEB_STACK"
  aws cloudformation wait stack-delete-complete --stack-name "$WEB_STACK"
fi
if [[ -n "$ALIAS_ID" ]]; then
  info "Deleting alias $ALIAS_ID"
  aws gamelift delete-alias --alias-id "$ALIAS_ID"
fi
for f in "${FLEETS[@]:-}"; do
  [[ -n "$f" ]] || continue
  info "Deleting fleet $f"
  aws gamelift delete-fleet --fleet-id "$f"
done
if ((DEL_ART)); then
  ART_BUCKET="$(artifact_bucket_name)"
  if aws s3api head-bucket --bucket "$ART_BUCKET" 2>/dev/null; then
    info "Deleting artifacts bucket $ART_BUCKET"
    aws s3 rb "s3://$ART_BUCKET" --force
  fi
fi
rm -f "$OUT_DIR/state.env" "$OUT_DIR/api_url.txt"
info "Done. Fleet deletion finishes in the background: aws gamelift describe-fleet-attributes --query 'FleetAttributes[].[FleetId,Status]'"
