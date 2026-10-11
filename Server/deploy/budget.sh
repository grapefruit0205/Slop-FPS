#!/usr/bin/env bash
# Deploy the monthly GameLift cost budget (80% actual / 100% forecasted e-mail alerts).
#   ./budget.sh [--yes]      uses BUDGET_AMOUNT and BUDGET_EMAIL from config.env
set -euo pipefail
# shellcheck source=lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

usage() { echo "Usage: $0 [--yes] [--account-id ID] [--profile P] [--region R]"; }

parse_args "$@"
finish_flags
load_config
[[ -n "$BUDGET_EMAIL" ]] || die "set BUDGET_EMAIL in $CONFIG_FILE"

confirm_account "Deploy stack $BUDGET_STACK: monthly \$$BUDGET_AMOUNT budget for Amazon GameLift, alerts to $BUDGET_EMAIL"
aws cloudformation deploy \
  --stack-name "$BUDGET_STACK" \
  --template-file "$INFRA_DIR/budget.yaml" \
  --no-fail-on-empty-changeset \
  --tags "Project=$PROJECT" \
  --parameter-overrides "Amount=$BUDGET_AMOUNT" "Email=$BUDGET_EMAIL"
info "Budget deployed. Note: budgets only alert, they never stop spending (see README for the capacity cap)."
