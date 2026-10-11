#!/usr/bin/env bash
# Change the capacity of the fleet behind the alias, and optionally manage auto scaling.
#
#   ./set-capacity.sh [min max desired] [--yes]          (defaults: CAPACITY_* from config.env)
#   ./set-capacity.sh --scaling-policy                   target-based policy, see below
#   ./set-capacity.sh --remove-scaling-policy
#   ./set-capacity.sh --status
#
# Examples:  ./set-capacity.sh 0 0 0   -> stop everything (no instance cost, lobby returns FLEET_FULL)
#            ./set-capacity.sh 0 2 1   -> default: one instance (4 rooms), may grow to 2
#
# --scaling-policy creates a TargetBased policy on PercentAvailableGameSessions
# (target = SCALING_TARGET_PERCENT_AVAILABLE, default 25): GameLift tries to keep ~25% of
# game-session slots free. Free slots < target => desired grows; far above => shrinks. It only
# moves DESIRED between min and max, so the hard cap (max) still applies. With min=0 and
# desired=0 there are no slots to measure, so it never starts the first instance by itself.
set -euo pipefail
# shellcheck source=lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

usage() {
  cat <<USAGE
Usage: $0 [<min> <max> <desired>] [--yes] [--account-id ID] [--profile P] [--region R]
       $0 --scaling-policy | --remove-scaling-policy | --status
USAGE
}

parse_args "$@"
MODE=capacity
take_flag --scaling-policy && MODE=policy
take_flag --remove-scaling-policy && MODE=unpolicy
take_flag --status && MODE=status
finish_flags
load_config

ALIAS_ID="$(find_alias_id)"
[[ -n "$ALIAS_ID" ]] || die "alias $ALIAS_NAME not found - run deploy-server.sh first"
FLEET_ID="$(alias_fleet_id "$ALIAS_ID")"
[[ -n "$FLEET_ID" ]] || die "alias $ALIAS_ID has no fleet"
POLICY_NAME="${PROJECT}-target-available"

case "$MODE" in
  status)
    aws gamelift describe-fleet-capacity --fleet-ids "$FLEET_ID" \
      --query 'FleetCapacity[0].[FleetId,InstanceType,InstanceCounts]' --output json
    aws gamelift describe-fleet-utilization --fleet-ids "$FLEET_ID" --output json
    exit 0 ;;
  capacity)
    case ${#POSITIONAL[@]} in
      0) MIN=$CAPACITY_MIN; MAX=$CAPACITY_MAX; DES=$CAPACITY_DESIRED ;;
      3) MIN=${POSITIONAL[0]}; MAX=${POSITIONAL[1]}; DES=${POSITIONAL[2]} ;;
      *) usage >&2; exit 2 ;;
    esac
    confirm_account "Set capacity of $FLEET_ID to min=$MIN max=$MAX desired=$DES"
    apply_capacity "$FLEET_ID" "$MIN" "$MAX" "$DES"
    echo "Players cap = $MAX instances x $SERVER_PROCESS_COUNT rooms x $MAX_PLAYERS players = $((MAX * SERVER_PROCESS_COUNT * MAX_PLAYERS))" ;;
  policy)
    confirm_account "Create target-based scaling policy ($SCALING_TARGET_PERCENT_AVAILABLE% available game sessions) on $FLEET_ID"
    aws gamelift put-scaling-policy --name "$POLICY_NAME" --fleet-id "$FLEET_ID" \
      --policy-type TargetBased --metric-name PercentAvailableGameSessions \
      --target-configuration "TargetValue=$SCALING_TARGET_PERCENT_AVAILABLE"
    echo "Policy $POLICY_NAME active. It scales DESIRED within [min,max]; remove with --remove-scaling-policy." ;;
  unpolicy)
    confirm_account "Delete scaling policy $POLICY_NAME on $FLEET_ID"
    aws gamelift delete-scaling-policy --name "$POLICY_NAME" --fleet-id "$FLEET_ID" ;;
esac
