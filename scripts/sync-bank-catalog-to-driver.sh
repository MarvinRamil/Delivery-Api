#!/usr/bin/env bash
# Regenerates bee-driver/shared/constants/banks.ts from PhBanks.json.
#
# The driver app's copy is a fallback only: the picker fetches GET /api/payments/banks and
# falls back to this constant when the request fails or the device is offline. Keeping it
# generated (rather than hand-maintained) is what stops the two lists drifting apart.
#
# Run after ./scripts/fetch-paymongo-banks.sh.
set -euo pipefail

cd "$(dirname "$0")/.."
SRC=src/Modules/BeeLogistics.Modules.Payment/Application/Banks/PhBanks.json
DEST=../bee-driver/shared/constants/banks.ts

[ -f "$SRC" ]  || { echo "missing $SRC" >&2; exit 1; }
[ -d "$(dirname "$DEST")" ] || { echo "missing $(dirname "$DEST") — is bee-driver checked out next to bee-backend?" >&2; exit 1; }

python3 - "$SRC" "$DEST" <<'PY'
import json, sys

src, dest = sys.argv[1], sys.argv[2]
with open(src, encoding="utf8") as fh:
    catalog = json.load(fh)

# Only institutions we can actually address; the API filters these out too.
banks = [b for b in catalog["banks"] if b.get("bic")]
banks.sort(key=lambda b: (b["type"] != "ewallet", b["name"].casefold()))

lines = [
    "/**",
    " * Philippine banks and e-wallets that can receive a driver payout.",
    " *",
    " * GENERATED FILE — do not edit by hand.",
    " * Source: bee-backend/src/Modules/BeeLogistics.Modules.Payment/Application/Banks/PhBanks.json",
    " * Regenerate: bee-backend/scripts/sync-bank-catalog-to-driver.sh",
    " *",
    " * This is the offline fallback for the withdrawal picker. `useBanks()` prefers",
    " * GET /api/payments/banks so a corrected BIC ships without an app release.",
    " */",
    "",
    "export interface PhBank {",
    "  /** Stable identifier sent back to the API as `bankCode`. */",
    "  code: string;",
    "  name: string;",
    "  /** SWIFT/BIC the payout is addressed to (InstaPay code where the two differ). */",
    "  bic: string;",
    "  /** PayMongo's registered name, when it differs from the display name above. */",
    "  legalName?: string;",
    "  /** Can receive on InstaPay: real-time, 24/7, max PHP 50,000 per transfer. */",
    "  instapay: boolean;",
    "  /** Can receive on PESONet: same or next banking day, max PHP 10,000,000. */",
    "  pesonet: boolean;",
    "  type: 'bank' | 'ewallet';",
    "}",
    "",
    "/** Per-transaction ceiling of each rail, per PayMongo. */",
    "export const RAIL_LIMITS = {",
    "  INSTAPAY: 50_000,",
    "  PESONET: 10_000_000,",
    "} as const;",
    "",
    "/** The fastest rail an institution supports decides how much it can take at once. */",
    "export function maxAmountFor(bank: Pick<PhBank, 'instapay' | 'pesonet'>): number {",
    "  return bank.instapay ? RAIL_LIMITS.INSTAPAY : RAIL_LIMITS.PESONET;",
    "}",
    "",
    "/** E-wallets first, then banks — the order the picker renders them in. */",
    "export const PH_BANKS: readonly PhBank[] = [",
]

for b in banks:
    def esc(v):
        return v.replace("\\", "\\\\").replace("'", "\\'")

    legal = f" legalName: '{esc(b['legalName'])}'," if b.get("legalName") else ""
    lines.append(
        f"  {{ code: '{b['code']}', name: '{esc(b['name'])}', bic: '{b['bic']}',{legal} "
        f"instapay: {str(b['instapay']).lower()}, pesonet: {str(b['pesonet']).lower()}, "
        f"type: '{b['type']}' }},"
    )

lines += [
    "] as const;",
    "",
    "export const PH_BANKS_BY_CODE: ReadonlyMap<string, PhBank> = new Map(",
    "  PH_BANKS.map((bank) => [bank.code, bank]),",
    ");",
    "",
]

with open(dest, "w", encoding="utf8") as fh:
    fh.write("\n".join(lines))

print(f"wrote {len(banks)} institutions to {dest}")
PY
