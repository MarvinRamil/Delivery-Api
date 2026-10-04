#!/usr/bin/env bash
# Prints the three PAYMONGO_SOURCE_ACCOUNT_* lines to paste into .env, read from the
# wallet the given key can see. Without all three, PayMongoOptions.DisbursementsConfigured
# is false and every driver withdrawal fails before reaching PayMongo.
#
#   ./scripts/paymongo-source-account.sh                  (reads the key from .env)
#   PAYMONGO_SECRET_KEY=sk_test_xxx ./scripts/paymongo-source-account.sh
#
# The key decides which mode you get: a test key sees only test-mode wallets, a live key
# only live ones. They are separate wallets and their account numbers are NOT interchangeable.
set -euo pipefail

cd "$(dirname "$0")/.."

# Falls back to .env (gitignored) so a live key never has to be typed on a command line,
# where it would land in shell history and in any transcript of this session.
KEY="${PAYMONGO_SECRET_KEY:-}"
if [ -z "$KEY" ] && [ -f .env ]; then
  KEY=$(sed -n 's/^PAYMONGO_SECRET_KEY=//p' .env | head -n1 | tr -d '"'"'"' \r')
  [ -n "$KEY" ] && echo "# using PAYMONGO_SECRET_KEY from .env" >&2
fi
[ -n "$KEY" ] || { echo "PAYMONGO_SECRET_KEY is not set, and .env has no usable value" >&2; exit 1; }

case "$KEY" in
  sk_test_*) MODE="test" ;;
  sk_live_*) MODE="live" ;;
  *) echo "PAYMONGO_SECRET_KEY must be a secret API key (sk_test_… / sk_live_…), got '${KEY:0:5}…'." >&2
     echo "A whsk_… value is the webhook signing secret and authenticates nothing." >&2; exit 1 ;;
esac

command -v jq >/dev/null || { echo "jq is required" >&2; exit 1; }

# Trailing slash: the API 301s without it and curl drops credentials across the redirect
# unless -L is given. The colon after the key is the empty password; omit it and curl prompts.
LIST=$(curl -sL --max-time 30 "https://api.paymongo.com/v2/wallets/" -u "$KEY:")

if [ "$(jq -r '.errors[0].code // empty' <<<"$LIST")" = "unauthorized" ]; then
  echo "PayMongo rejected the key: $(jq -r '.errors[0].detail' <<<"$LIST")" >&2
  exit 1
fi

COUNT=$(jq '(.data // []) | length' <<<"$LIST")

if [ "$COUNT" -eq 0 ]; then
  cat >&2 <<MSG
No wallet exists in ${MODE} mode for this account.

A wallet in one mode is invisible to the other's key, so a live wallet you can see in the
dashboard will not appear here with a test key. Either ask PayMongo to provision a
${MODE}-mode wallet, or run this with the key for the mode your wallet is in.

Until these three values are set, driver withdrawals fail at the DisbursementsConfigured
guard in PayMongoGateway.CreateDisbursementAsync.
MSG
  exit 2
fi

if [ "$COUNT" -gt 1 ]; then
  echo "# note: ${COUNT} wallets found; using the default one" >&2
fi

WALLET_ID=$(jq -r '(.data | map(select(.is_default)) | .[0] // .data[0] // .[0]).id' <<<"$LIST")
[ -n "$WALLET_ID" ] && [ "$WALLET_ID" != "null" ] || { echo "could not determine a wallet id" >&2; exit 1; }

# The account number and name live on the v1 wallet resource. /v2/wallets returns only
# status and ids -- despite the guide page telling you to read source_account off it --
# so this is a v1 call on purpose, not an oversight.
WALLET=$(curl -sL --max-time 30 "https://api.paymongo.com/v1/wallets/$WALLET_ID" -u "$KEY:")

NUMBER=$(jq -r '.data.attributes.account_number // empty' <<<"$WALLET")
NAME=$(jq -r '.data.attributes.account_name // empty' <<<"$WALLET")

if [ -z "$NUMBER" ] || [ -z "$NAME" ]; then
  echo "wallet $WALLET_ID returned no account_number/account_name:" >&2
  jq -r '.' <<<"$WALLET" >&2
  exit 1
fi

BALANCE=$(jq -r '.data.attributes.available_balance // 0' <<<"$WALLET")
echo "# wallet $WALLET_ID (${MODE} mode), available balance PHP $(awk "BEGIN{printf \"%.2f\", $BALANCE/100}")" >&2

# Every PayMongo wallet sends as PAEYPHM2XXX. Verified against this account's own
# succeeded transfers, where sender.bank_code is exactly that.
cat <<OUT
PAYMONGO_SOURCE_ACCOUNT_NUMBER=$NUMBER
PAYMONGO_SOURCE_ACCOUNT_NAME=$NAME
PAYMONGO_SOURCE_ACCOUNT_BIC=PAEYPHM2XXX
OUT
