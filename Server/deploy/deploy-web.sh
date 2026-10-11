#!/usr/bin/env bash
# Deploy the web stack (S3 + CloudFront) and upload a Unity WebGL build with the right
# Content-Type / Content-Encoding / Cache-Control metadata.
#
#   ./deploy-web.sh <webgl-build-dir> [--yes]
#   ./deploy-web.sh --stack-only [--yes]      only create/update the stack (to learn the site URL)
#
# Supports uncompressed, Brotli (.br) and Gzip (.gz) Unity builds, plus legacy .unityweb.
set -euo pipefail
# shellcheck source=lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

usage() {
  cat <<USAGE
Usage: $0 <webgl-build-dir> [--yes] [--account-id ID] [--profile P] [--region R]
       $0 --stack-only [--yes]
USAGE
}

parse_args "$@"
STACK_ONLY=0
take_flag --stack-only && STACK_ONLY=1
finish_flags
load_config

if ((STACK_ONLY)); then
  DIR=""
else
  ((${#POSITIONAL[@]} == 1)) || { usage >&2; exit 2; }
  DIR="${POSITIONAL[0]%/}"
  [[ -f "$DIR/index.html" ]] || die "$DIR/index.html not found - point at the Unity WebGL build output folder"
fi

confirm_account "Deploy stack $WEB_STACK (S3 + CloudFront)${DIR:+ and upload $DIR}"

info "Deploying stack $WEB_STACK"
aws cloudformation deploy \
  --stack-name "$WEB_STACK" \
  --template-file "$INFRA_DIR/web.yaml" \
  --no-fail-on-empty-changeset \
  --tags "Project=$PROJECT" \
  --parameter-overrides "ProjectName=$PROJECT"

BUCKET="$(stack_output "$WEB_STACK" BucketName)"
DIST_ID="$(stack_output "$WEB_STACK" DistributionId)"
SITE_URL="$(stack_output "$WEB_STACK" SiteUrl)"
[[ -n "$BUCKET" && -n "$DIST_ID" ]] || die "could not read stack outputs"

if ((STACK_ONLY)); then
  info "Stack ready. Site URL: $SITE_URL"
  echo "Next: ./deploy-backend.sh (adds this URL to CORS), then build Unity with the API URL and run: $0 <build-dir>"
  exit 0
fi

# ---- metadata rules ------------------------------------------------------------------
# Echoes "<content-type>|<content-encoding or empty>" for a path relative to the build root.
file_meta() {
  local f="$1" base="$1" enc="" ct
  case "$f" in
    *.br)       enc=br;   base="${f%.br}" ;;
    *.gz)       enc=gzip; base="${f%.gz}" ;;
    *.unityweb) # legacy Unity naming: encoding is not in the name, sniff the magic bytes
      base="${f%.unityweb}"
      if [[ "$(head -c2 "$DIR/$f" | od -An -tx1 | tr -d ' \n')" == "1f8b" ]]; then enc=gzip; else enc=br; fi ;;
  esac
  case "$base" in
    *.wasm)  ct="application/wasm" ;;
    *.js)    ct="application/javascript" ;;
    *.data)  ct="application/octet-stream" ;;
    *.json)  ct="application/json" ;;
    *.html)  ct="text/html; charset=utf-8" ;;
    *.css)   ct="text/css" ;;
    *.png)   ct="image/png" ;;
    *.jpg|*.jpeg) ct="image/jpeg" ;;
    *.svg)   ct="image/svg+xml" ;;
    *.ico)   ct="image/x-icon" ;;
    *.txt)   ct="text/plain; charset=utf-8" ;;
    *)       ct="application/octet-stream" ;;
  esac
  echo "$ct|$enc"
}

# Unity can name build files with a content hash ("Name Files As Hashes"). Hashed names are
# safe to cache for a year; fixed names (Build/FPS.wasm) are not, because browsers would keep
# serving the old game after a deploy (CloudFront invalidation does not clear browser caches).
HASHED=0
if find "$DIR/Build" -maxdepth 1 -type f 2>/dev/null | grep -qE '/[0-9a-f]{32}\.'; then HASHED=1; fi
if ((HASHED)); then
  BUILD_CACHE="public, max-age=31536000, immutable"
else
  BUILD_CACHE="public, max-age=3600, must-revalidate"
  warn "Build files are not hash-named: using a 1h cache for Build/*. Enable Player Settings > Publishing > 'Name files as hashes' for 1-year caching."
fi

upload() { # upload <relative-path>
  local rel="$1" meta ct enc cache args
  meta="$(file_meta "$rel")"; ct="${meta%%|*}"; enc="${meta##*|}"
  case "$rel" in
    index.html|*.html) cache="no-cache" ;;
    Build/*)           cache="$BUILD_CACHE" ;;
    *)                 cache="public, max-age=86400" ;;   # TemplateData, StreamingAssets, ...
  esac
  args=(--content-type "$ct" --cache-control "$cache" --only-show-errors)
  [[ -n "$enc" ]] && args+=(--content-encoding "$enc")
  printf '  %-60s %s%s\n' "$rel" "$ct" "${enc:+  [$enc]}"
  aws s3 cp "$DIR/$rel" "s3://$BUCKET/$rel" "${args[@]}"
}

info "Uploading $DIR -> s3://$BUCKET"
# Everything except HTML first, index.html last, so clients never see a page pointing at missing files.
while IFS= read -r -d '' path; do
  rel="${path#"$DIR"/}"
  [[ "$rel" == *.html || "$(basename "$rel")" == .DS_Store ]] && continue
  upload "$rel"
done < <(find "$DIR" -type f -print0 | LC_ALL=C sort -z)
while IFS= read -r -d '' path; do
  upload "${path#"$DIR"/}"
done < <(find "$DIR" -type f -name '*.html' -print0 | LC_ALL=C sort -z)

PATHS=(/ /index.html)
((HASHED)) || PATHS+=('/Build/*')
info "Invalidating ${PATHS[*]}"
aws cloudfront create-invalidation --distribution-id "$DIST_ID" --paths "${PATHS[@]}" \
  --query 'Invalidation.[Id,Status]' --output text

echo
info "Site URL: $SITE_URL"
