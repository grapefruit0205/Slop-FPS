#!/usr/bin/env bash
# Upload a Linux dedicated-server build to GameLift and (blue/green) roll out a fleet.
#
#   ./deploy-server.sh <server-build-dir> <version> [--yes] [--force] [--no-wait-processes]
#
# First run : upload build -> create fleet -> wait ACTIVE -> set capacity -> create alias.
# Later runs: upload build -> create NEW fleet -> wait ACTIVE -> set capacity -> wait for
#             server processes -> repoint alias to the new fleet. The old fleet is left
#             running; the script prints the commands to scale it down / delete it.
set -euo pipefail
# shellcheck source=lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

usage() {
  cat <<USAGE
Usage: $0 <server-build-dir> <version> [--yes] [--account-id ID] [--profile P] [--region R]
                                       [--force] [--no-wait-processes]
  <server-build-dir>  Linux server build folder containing FPSServer.x86_64
  <version>           free-form build version label, e.g. 0.3.1
  --force             deploy even if the alias already points at a fleet running this build
  --no-wait-processes do not wait for ready server processes before repointing the alias
USAGE
}

parse_args "$@"
FORCE=0; WAIT_PROC=1
take_flag --force && FORCE=1
take_flag --no-wait-processes && WAIT_PROC=0
finish_flags
((${#POSITIONAL[@]} == 2)) || { usage >&2; exit 2; }
BUILD_ROOT="${POSITIONAL[0]}"; VERSION="${POSITIONAL[1]}"
[[ -d "$BUILD_ROOT" ]] || die "build dir not found: $BUILD_ROOT"
load_config
LAUNCH_FILE="$BUILD_ROOT/$(basename "$SERVER_LAUNCH_PATH")"
[[ -f "$LAUNCH_FILE" ]] || die "$LAUNCH_FILE not found - is this the Linux dedicated server build?"
[[ -x "$LAUNCH_FILE" ]] || { warn "$LAUNCH_FILE is not executable; running chmod +x"; chmod +x "$LAUNCH_FILE"; }
[[ "$VERSION" =~ ^[A-Za-z0-9._+-]{1,40}$ ]] || die "version must match [A-Za-z0-9._+-]{1,40}"

confirm_account "Upload build $VERSION and create/replace the GameLift fleet (billed per instance hour)"

BUILD_NAME="${PROJECT}-server"
FLEET_NAME="${PROJECT}-${VERSION}"

# ---- 1. build (reuse a READY build with the same name+version) --------------------
BUILD_ID="$(aws gamelift list-builds --status READY \
  --query "Builds[?Name=='$BUILD_NAME' && Version=='$VERSION'].BuildId | [0]" --output text | none_to_empty)"
if [[ -n "$BUILD_ID" ]] && ! ((FORCE)); then
  info "Reusing existing READY build $BUILD_ID ($BUILD_NAME $VERSION)"
else
  info "Uploading build from $BUILD_ROOT (this zips and uploads the whole folder)"
  # NOTE: AMAZON_LINUX_2023 + server SDK 5.5.1 as requested; the CLI prints "Build ID: build-..."
  UPLOAD_OUT="$(aws gamelift upload-build \
    --name "$BUILD_NAME" --build-version "$VERSION" --build-root "$BUILD_ROOT" \
    --operating-system AMAZON_LINUX_2023 --server-sdk-version "$SERVER_SDK_VERSION" | tee >(cat >&2))"
  BUILD_ID="$(grep -oE 'build-[0-9a-fA-F-]+' <<<"$UPLOAD_OUT" | tail -1)"
  [[ -n "$BUILD_ID" ]] || die "could not parse build id from upload-build output"
fi
info "Build id: $BUILD_ID"

# ---- 2. current alias / fleet ----------------------------------------------------
ALIAS_ID="$(find_alias_id)"
OLD_FLEET=""
if [[ -n "$ALIAS_ID" ]]; then
  OLD_FLEET="$(alias_fleet_id "$ALIAS_ID")"
  info "Alias $ALIAS_NAME ($ALIAS_ID) currently -> ${OLD_FLEET:-<terminal routing>}"
  if [[ -n "$OLD_FLEET" ]] && ! ((FORCE)); then
    OLD_BUILD="$(aws gamelift describe-fleet-attributes --fleet-ids "$OLD_FLEET" --query 'FleetAttributes[0].BuildId' --output text)"
    if [[ "$OLD_BUILD" == "$BUILD_ID" ]]; then
      info "Alias already points at a fleet running build $BUILD_ID. Nothing to do (use --force to redeploy)."
      exit 0
    fi
  fi
fi

# ---- 3. fleet (resume a half-done previous run if a fleet for this build exists) ------
NEW_FLEET=""
for f in $(aws gamelift list-fleets --build-id "$BUILD_ID" --query 'FleetIds' --output text | none_to_empty); do
  [[ "$f" == "$OLD_FLEET" ]] && continue
  st="$(fleet_status "$f")"
  if [[ "$st" != ERROR && "$st" != DELETING && "$st" != TERMINATED ]] && ! ((FORCE)); then NEW_FLEET="$f"; break; fi
done

if [[ -n "$NEW_FLEET" ]]; then
  info "Resuming with existing fleet $NEW_FLEET for this build"
else
  # RuntimeConfiguration: one entry per server process, each with its own port + log file.
  PROCS=""
  for ((i = 0; i < SERVER_PROCESS_COUNT; i++)); do
    port=$((SERVER_BASE_PORT + i))
    [[ -n "$PROCS" ]] && PROCS+=","
    PROCS+="{\"LaunchPath\":\"$SERVER_LAUNCH_PATH\",\"Parameters\":\"-batchmode -nographics -port $port -logFile /local/game/logs/server-$port.log\",\"ConcurrentExecutions\":1}"
  done
  LAST_PORT=$((SERVER_BASE_PORT + SERVER_PROCESS_COUNT - 1))
  FLEET_JSON="$OUT_DIR/create-fleet.json"
  # LogPaths is intentionally absent: it is "no longer used" by the API. Log upload
  # directories must be passed by the server via ProcessReady(logParameters) in the Server SDK.
  cat >"$FLEET_JSON" <<JSON
{
  "Name": "$FLEET_NAME",
  "Description": "$PROJECT dedicated server $VERSION",
  "BuildId": "$BUILD_ID",
  "ComputeType": "EC2",
  "FleetType": "ON_DEMAND",
  "EC2InstanceType": "$EC2_INSTANCE_TYPE",
  "CertificateConfiguration": { "CertificateType": "GENERATED" },
  "EC2InboundPermissions": [
    { "FromPort": $SERVER_BASE_PORT, "ToPort": $LAST_PORT, "IpRange": "0.0.0.0/0", "Protocol": "TCP" }
  ],
  "NewGameSessionProtectionPolicy": "NoProtection",
  "RuntimeConfiguration": {
    "ServerProcesses": [$PROCS],
    "MaxConcurrentGameSessionActivations": $SERVER_PROCESS_COUNT,
    "GameSessionActivationTimeoutSeconds": 120
  },
  "Tags": [ { "Key": "Project", "Value": "$PROJECT" } ]
}
JSON
  info "Creating fleet $FLEET_NAME ($EC2_INSTANCE_TYPE, $SERVER_PROCESS_COUNT processes, TCP $SERVER_BASE_PORT-$LAST_PORT)"
  NEW_FLEET="$(aws gamelift create-fleet --cli-input-json "file://$FLEET_JSON" \
    --query 'FleetAttributes.FleetId' --output text)"
  [[ "$NEW_FLEET" == fleet-* ]] || die "create-fleet returned unexpected id: $NEW_FLEET"
  info "Created $NEW_FLEET"
fi

wait_fleet_active "$NEW_FLEET" 2400 || die "fleet $NEW_FLEET did not become ACTIVE"

# ---- 4. capacity -------------------------------------------------------------------
apply_capacity "$NEW_FLEET" "$CAPACITY_MIN" "$CAPACITY_MAX" "$CAPACITY_DESIRED"

# ---- 5. wait for at least one ready server process before switching players over ----
if [[ -n "$OLD_FLEET" ]] && ((WAIT_PROC)) && ((CAPACITY_DESIRED > 0)); then
  info "Waiting up to 15 min for a ready server process on $NEW_FLEET before repointing the alias"
  ok=0
  for _ in $(seq 1 45); do
    n="$(aws gamelift describe-fleet-utilization --fleet-ids "$NEW_FLEET" \
      --query 'FleetUtilization[0].ActiveServerProcessCount' --output text | none_to_empty)"
    echo "    active server processes: ${n:-0}"
    if [[ "${n:-0}" =~ ^[0-9]+$ ]] && ((${n:-0} > 0)); then ok=1; break; fi
    sleep 20
  done
  ((ok)) || die "no server process became ready on $NEW_FLEET - alias NOT repointed. Check the server build/logs, then re-run."
fi

# ---- 6. alias ------------------------------------------------------------------------
if [[ -z "$ALIAS_ID" ]]; then
  info "Creating alias $ALIAS_NAME -> $NEW_FLEET"
  ALIAS_ID="$(aws gamelift create-alias --name "$ALIAS_NAME" \
    --description "$PROJECT current fleet" \
    --routing-strategy "Type=SIMPLE,FleetId=$NEW_FLEET" \
    --query 'Alias.AliasId' --output text)"
else
  info "Repointing alias $ALIAS_ID -> $NEW_FLEET"
  aws gamelift update-alias --alias-id "$ALIAS_ID" \
    --routing-strategy "Type=SIMPLE,FleetId=$NEW_FLEET" >/dev/null
fi

printf 'ALIAS_ID=%s\nFLEET_ID=%s\nBUILD_ID=%s\nVERSION=%s\n' "$ALIAS_ID" "$NEW_FLEET" "$BUILD_ID" "$VERSION" >"$OUT_DIR/state.env"

echo
info "Done. Alias $ALIAS_NAME = $ALIAS_ID -> fleet $NEW_FLEET (build $BUILD_ID)"
if [[ -n "$OLD_FLEET" && "$OLD_FLEET" != "$NEW_FLEET" ]]; then
  cat <<EOF2

The previous fleet $OLD_FLEET is STILL RUNNING (and billing). New players now land on the
new fleet. Once the old fleet has no players, retire it - NOT run automatically:

  # check it is idle
  aws gamelift describe-fleet-utilization --fleet-ids $OLD_FLEET --query 'FleetUtilization[0].[CurrentPlayerSessionCount,ActiveGameSessionCount]'
  # then delete it
  aws gamelift delete-fleet --fleet-id $OLD_FLEET${AWS_PROFILE:+ --profile $AWS_PROFILE} --region $AWS_REGION

Rollback instead: aws gamelift update-alias --alias-id $ALIAS_ID --routing-strategy Type=SIMPLE,FleetId=$OLD_FLEET
EOF2
fi
echo "Next: ./deploy-backend.sh (uses alias $ALIAS_ID)"
