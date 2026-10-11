#!/usr/bin/env bash
# Shared helpers, sourced by the other scripts. Not meant to be executed.
# shellcheck shell=bash

DEPLOY_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SERVER_DIR="$(dirname "$DEPLOY_DIR")"
INFRA_DIR="$SERVER_DIR/infra"
OUT_DIR="$DEPLOY_DIR/out"
BUILD_DIR="$DEPLOY_DIR/.build"
CONFIG_FILE="${CONFIG_FILE:-$DEPLOY_DIR/config.env}"

die()  { echo "ERROR: $*" >&2; exit 1; }
info() { echo "==> $*"; }
warn() { echo "WARN: $*" >&2; }

YES=0
ASSERT_ACCOUNT=""
CLI_PROFILE=""
CLI_REGION=""
POSITIONAL=()

# Parses the flags shared by all scripts; everything else lands in POSITIONAL.
#   --yes | -y            skip the interactive confirmation
#   --account-id <id>     required together with --yes for strict profiles / teardown
#   --profile <name>      overrides AWS_PROFILE from config.env
#   --region <name>       overrides AWS_REGION from config.env
parse_args() {
  POSITIONAL=()
  while (($#)); do
    case "$1" in
      --yes|-y) YES=1 ;;
      --account-id) shift; ASSERT_ACCOUNT="${1:-}" ;;
      --profile) shift; CLI_PROFILE="${1:-}" ;;
      --region) shift; CLI_REGION="${1:-}" ;;
      -h|--help) usage; exit 0 ;;
      --) shift; POSITIONAL+=("$@"); break ;;
      -*) EXTRA_FLAGS+=("$1") ;;
      *) POSITIONAL+=("$1") ;;
    esac
    shift
  done
}
EXTRA_FLAGS=()

# take_flag --foo : true (and consumes it) if the script-specific flag was given.
take_flag() {
  local i
  for i in "${!EXTRA_FLAGS[@]}"; do
    if [[ "${EXTRA_FLAGS[$i]}" == "$1" ]]; then unset 'EXTRA_FLAGS[i]'; EXTRA_FLAGS=("${EXTRA_FLAGS[@]}"); return 0; fi
  done
  return 1
}
# Call after all take_flag calls: typos should fail loudly, not be ignored.
finish_flags() { ((${#EXTRA_FLAGS[@]} == 0)) || { usage >&2; die "unknown option(s): ${EXTRA_FLAGS[*]}"; }; }

load_config() {
  command -v aws >/dev/null || die "aws CLI not found in PATH"
  # precedence: CLI flag > environment > config.env
  local env_profile="${AWS_PROFILE:-}" env_region="${AWS_REGION:-}"
  if [[ -f "$CONFIG_FILE" ]]; then
    set -a
    # shellcheck disable=SC1090
    source "$CONFIG_FILE"
    set +a
  else
    die "config not found: $CONFIG_FILE  (cp config.env.example config.env and edit it)"
  fi
  [[ -n "$env_profile" ]] && AWS_PROFILE="$env_profile"
  [[ -n "$env_region" ]] && AWS_REGION="$env_region"
  [[ -n "$CLI_PROFILE" ]] && AWS_PROFILE="$CLI_PROFILE"
  [[ -n "$CLI_REGION" ]] && AWS_REGION="$CLI_REGION"

  : "${AWS_REGION:=ap-northeast-2}"
  : "${STRICT_PROFILES:=*prod* *production*}"
  : "${PROJECT:=fps}"
  : "${ALIAS_NAME:=fps-server}"
  : "${BACKEND_STACK:=fps-backend}"
  : "${WEB_STACK:=fps-web}"
  : "${BUDGET_STACK:=fps-budget}"
  : "${EC2_INSTANCE_TYPE:=c6i.large}"
  : "${SERVER_SDK_VERSION:=5.5.1}"
  : "${SERVER_LAUNCH_PATH:=/local/game/FPSServer.x86_64}"
  : "${SERVER_BASE_PORT:=7770}"
  : "${SERVER_PROCESS_COUNT:=4}"
  : "${CAPACITY_MIN:=0}"
  : "${CAPACITY_MAX:=2}"
  : "${CAPACITY_DESIRED:=1}"
  : "${MAX_INSTANCES_LIMIT:=2}"
  : "${SCALING_TARGET_PERCENT_AVAILABLE:=25}"
  : "${MAX_PLAYERS:=8}"
  : "${ALLOWED_ORIGINS:=http://localhost:8080}"
  : "${BUDGET_AMOUNT:=50}"
  : "${BUDGET_EMAIL:=}"

  if [[ -z "${AWS_PROFILE:-}" ]]; then unset AWS_PROFILE; else export AWS_PROFILE; fi
  export AWS_REGION AWS_DEFAULT_REGION="$AWS_REGION" AWS_PAGER=""
  mkdir -p "$OUT_DIR" "$BUILD_DIR"
}

# Prints who/where we are and asks before anything is created or deleted.
# $1 = what is about to happen, $2 = "strict" to always require typing the account id.
confirm_account() {
  local what="$1" strict="${2:-}" ident
  ident="$(aws sts get-caller-identity --query '[Account,Arn]' --output text)" \
    || die "cannot determine caller identity (expired credentials? try: aws sso login --profile ${AWS_PROFILE:-<profile>})"
  ACCOUNT_ID="${ident%%[[:space:]]*}"
  local arn="${ident#*[[:space:]]}"
  echo "------------------------------------------------------------"
  echo " AWS account : $ACCOUNT_ID"
  echo " Caller      : $arn"
  echo " Profile     : ${AWS_PROFILE:-<default credential chain>}"
  echo " Region      : $AWS_REGION"
  echo " Action      : $what"
  echo "------------------------------------------------------------"

  if [[ -n "${EXPECTED_ACCOUNT_ID:-}" && "$EXPECTED_ACCOUNT_ID" != "$ACCOUNT_ID" ]]; then
    die "account $ACCOUNT_ID does not match EXPECTED_ACCOUNT_ID=$EXPECTED_ACCOUNT_ID"
  fi
  # Patterns are shell globs (e.g. "*prod*"); read them without letting the shell expand them against files.
  local p pats
  read -ra pats <<<"$STRICT_PROFILES"
  for p in "${pats[@]}"; do
    # shellcheck disable=SC2053  # unquoted on purpose: glob match
    [[ -n "${AWS_PROFILE:-}" && "${AWS_PROFILE}" == $p ]] && strict=strict
  done
  [[ "$strict" == strict ]] && echo " (strict mode: account id must be confirmed)"

  if ((YES)); then
    if [[ "$strict" == strict && "$ASSERT_ACCOUNT" != "$ACCOUNT_ID" ]]; then
      die "strict mode: --yes needs --account-id $ACCOUNT_ID"
    fi
    return 0
  fi
  [[ -t 0 ]] || die "not interactive: re-run with --yes$([[ $strict == strict ]] && echo " --account-id $ACCOUNT_ID")"
  local ans
  if [[ "$strict" == strict ]]; then
    read -r -p "Type the account id ($ACCOUNT_ID) to continue: " ans
    [[ "$ans" == "$ACCOUNT_ID" ]] || die "aborted"
  else
    read -r -p "Proceed? [y/N] " ans
    [[ "$ans" =~ ^[Yy]$ ]] || die "aborted"
  fi
}

# ---- GameLift helpers ---------------------------------------------------------
none_to_empty() { local v; read -r v || true; [[ "$v" == "None" || "$v" == "null" ]] && v=""; echo "$v"; }

find_alias_id() {
  aws gamelift list-aliases --name "$ALIAS_NAME" \
    --query "Aliases[?Name=='$ALIAS_NAME'].AliasId | [0]" --output text | none_to_empty
}

alias_fleet_id() {
  aws gamelift describe-alias --alias-id "$1" \
    --query 'Alias.RoutingStrategy.FleetId' --output text | none_to_empty
}

fleet_status() {
  aws gamelift describe-fleet-attributes --fleet-ids "$1" \
    --query 'FleetAttributes[0].Status' --output text
}

# Polls until the fleet is ACTIVE (default timeout 40 min). Prints events on ERROR.
wait_fleet_active() {
  local fleet="$1" timeout="${2:-2400}" interval=30 waited=0 status
  info "Waiting for fleet $fleet to become ACTIVE (timeout ${timeout}s)"
  while ((waited <= timeout)); do
    status="$(fleet_status "$fleet")"
    echo "    [$(date +%H:%M:%S)] status=$status"
    case "$status" in
      ACTIVE) return 0 ;;
      ERROR|TERMINATED)
        warn "Fleet entered $status. Recent events:"
        aws gamelift describe-fleet-events --fleet-id "$fleet" --max-items 25 \
          --query 'Events[].[EventTime,EventCode,Message]' --output text >&2 || true
        return 1 ;;
    esac
    sleep "$interval"; waited=$((waited + interval))
  done
  warn "Timed out waiting for fleet $fleet (last status: $status)"
  aws gamelift describe-fleet-events --fleet-id "$fleet" --max-items 15 \
    --query 'Events[].[EventTime,EventCode,Message]' --output text >&2 || true
  return 1
}

# apply_capacity <fleet-id> <min> <max> <desired>   (validates against the hard limit)
apply_capacity() {
  local fleet="$1" min="$2" max="$3" des="$4"
  [[ "$min" =~ ^[0-9]+$ && "$max" =~ ^[0-9]+$ && "$des" =~ ^[0-9]+$ ]] || die "capacity values must be non-negative integers"
  ((min <= des && des <= max)) || die "need min <= desired <= max (got $min/$des/$max)"
  if ((max > MAX_INSTANCES_LIMIT)); then
    die "max=$max exceeds MAX_INSTANCES_LIMIT=$MAX_INSTANCES_LIMIT (the deliberate cost cap). Raise MAX_INSTANCES_LIMIT in config.env if you really want this."
  fi
  info "Fleet $fleet capacity: min=$min max=$max desired=$des"
  aws gamelift update-fleet-capacity --fleet-id "$fleet" \
    --min-size "$min" --max-size "$max" --desired-instances "$des" >/dev/null
}

# Looks up a CloudFormation stack output; empty if the stack or output is missing.
stack_output() {
  aws cloudformation describe-stacks --stack-name "$1" \
    --query "Stacks[0].Outputs[?OutputKey=='$2'].OutputValue | [0]" --output text 2>/dev/null | none_to_empty || true
}
stack_exists() {
  aws cloudformation describe-stacks --stack-name "$1" >/dev/null 2>&1
}

artifact_bucket_name() {
  echo "${ARTIFACT_BUCKET:-${PROJECT}-artifacts-${ACCOUNT_ID}-${AWS_REGION}}"
}
