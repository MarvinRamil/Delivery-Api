#!/usr/bin/env bash
# Regenerates PhBanks.json from PayMongo's live receiving-institutions list.
#
# The catalog checked into the repo is a SEED: names and per-rail support come from
# https://docs.paymongo.com/docs/money-movement-list-of-banks, which publishes no codes,
# so only the 12 BICs previously verified by hand are filled in. Every other entry has
# "bic": null and is withheld from GET /api/payments/banks — a guessed BIC would send a
# driver's money to the wrong institution. Run this once with a real secret key to
# complete it.
#
#   PAYMONGO_SECRET_KEY=sk_test_xxx ./scripts/fetch-paymongo-banks.sh
#
# The two PayMongo docs pages disagree on the path, so both are tried in order:
#   GET /v2/transfers/receiving_institutions?provider=…   (guide: "Move money with API")
#   GET /v1/wallets/receiving_institutions?provider=…     (API reference)
set -euo pipefail

cd "$(dirname "$0")/.."
OUT=src/Modules/BeeLogistics.Modules.Payment/Application/Banks/PhBanks.json
KEY="${PAYMONGO_SECRET_KEY:-}"

[ -n "$KEY" ] || { echo "PAYMONGO_SECRET_KEY is not set" >&2; exit 1; }
case "$KEY" in
  sk_test_*|sk_live_*) ;;
  *) echo "PAYMONGO_SECRET_KEY must be a secret API key (sk_test_… / sk_live_…), got '${KEY:0:5}…'." >&2
     echo "A whsk_… value is the webhook signing secret and authenticates nothing." >&2; exit 1 ;;
esac

command -v jq >/dev/null || { echo "jq is required" >&2; exit 1; }

TMP=$(mktemp -d); trap 'rm -rf "$TMP"' EXIT

fetch() { # $1=rail -> writes $TMP/$1.json, echoes the path that answered
  local rail=$1 url code
  for url in "https://api.paymongo.com/v2/transfers/receiving_institutions?provider=$rail" \
             "https://api.paymongo.com/v1/wallets/receiving_institutions?provider=$rail"; do
    code=$(curl -sS -L --max-time 30 -o "$TMP/$rail.json" -w '%{http_code}' "$url" -u "$KEY:")
    if [ "$code" = 200 ]; then echo "$url"; return 0; fi
    echo "  $url -> HTTP $code" >&2
  done
  echo "both receiving_institutions paths failed for $rail" >&2
  return 1
}

echo "Fetching InstaPay institutions…"; INSTAPAY_URL=$(fetch instapay)
echo "Fetching PESONet institutions…"; PESONET_URL=$(fetch pesonet)

python3 scripts/merge_paymongo_banks.py \
  "$TMP/instapay.json" "$TMP/pesonet.json" "$OUT" "$INSTAPAY_URL"

echo "Wrote $OUT"
echo "Now regenerate the driver-app copy:  ./scripts/sync-bank-catalog-to-driver.sh"
