#!/usr/bin/env python3
"""Merges PayMongo's two receiving-institution lists into PhBanks.json.

Called by fetch-paymongo-banks.sh. Response shape (v1), confirmed against the live API:

    {"has_more": false,
     "data": [{"id": "10", "type": "receiving_institution",
               "attributes": {"name": "BDO Unibank, Inc.", "provider": "instapay",
                              "provider_code": "BNORPHMMXXX", "type": ["sender", "receiver"]}}]}

Notes that drove the code below:

* `provider_code` is the 11-character BIC and is what goes in `destination_account.bic`.
  It is NOT always an 8-char BIC plus "XXX" -- PNB is `PNBMPHMMTOD` -- so codes are used
  exactly as returned and never reconstructed.
* `attributes.type` is populated for InstaPay but is an empty list for every PESONet
  record, so it cannot be used to filter. Everything this endpoint returns is a receiving
  institution by definition, which is the filter that matters for payouts.
* Legacy friendly codes (BPI, BDO, GCASH, ...) are re-attached by matching the first 8
  characters of the live code against the BIC that code used to resolve to. Matching on
  name would fail -- PayMongo returns legal names ("G-Xchange, Inc.") that do not
  resemble the names those codes were introduced with.
"""
import json
import re
import sys
import unicodedata

# Legacy friendly code -> the 8-char BIC it resolved to before the catalog existed.
# These codes are stored on live WithdrawalRequest / SavedWithdrawalMethod rows, so they
# must keep resolving to the same institution. Joined on prefix, not equality.
LEGACY_CODES = {
    "BOPIPHMM": "BPI",
    "BNORPHMM": "BDO",
    "UBPHPHMM": "UBP",
    "GXCHPHM2": "GCASH",
    "SETCPHMM": "SEC",
    "PAPHPHM1": "MAYA",
    "TLBPPHMM": "LANDBANK",
    "MBTCPHMM": "METROBANK",
    "PNBMPHMM": "PNB",
    "RCBCPHMM": "RCBC",
    "CHBKPHMM": "CHINABANK",
    "EWBCPHMM": "EASTWEST",
}

# PayMongo returns registered legal names. For consumer brands those are unrecognisable in
# a picker -- a driver looking for GCash will not find "G-Xchange, Inc." -- so the handful
# of wallets people know by brand get a display name. Keyed by BIC prefix, since the legal
# names themselves change. The BIC, not the name, is what addresses the transfer.
DISPLAY_NAMES = {
    "GXCHPHM2": "GCash",
    "PAPHPHM1": "Maya",
    "DCPHPHM1": "Coins.ph",
}

# Institutions that are e-money issuers rather than banks. Only decides which section of
# the picker an entry appears under, so a name heuristic is acceptable here -- a
# misclassification moves a row, it cannot misroute money.
EWALLET_HINTS = (
    "alipay", "bananapay", "bayad", "coins.ph", "dcpay", "easy pay", "g-xchange", "gcash",
    "grabpay", "i-remit", "lulu financial", "marcopay", "maya philippines", "omnipay",
    "peppermint", "pps-pepp", "shopeepay", "speedypay", "starpay", "tayocash", "tokTok",
    "toktok", "topjuan", "traxion", "ussc", "wise pilipinas", "zybi", "digital asset exchange",
)


def records(payload):
    data = payload.get("data") if isinstance(payload, dict) else payload
    if not isinstance(data, list):
        raise SystemExit(f"unexpected receiving_institutions shape: {type(data).__name__}")
    for item in data:
        attrs = item.get("attributes") or {}
        name = attrs.get("name")
        code = attrs.get("provider_code")
        if not name or not code:
            raise SystemExit(
                f"record {item.get('id')} has no name/provider_code; keys were {sorted(attrs)}"
            )
        yield item.get("id"), name.strip(), code.strip()


def slug(name):
    n = unicodedata.normalize("NFKD", name).encode("ascii", "ignore").decode()
    n = re.sub(r"\([^)]*\)", " ", n)
    return re.sub(r"[^A-Za-z0-9]+", "_", n).strip("_").upper()


def main():
    instapay_path, pesonet_path, out_path, source_url = sys.argv[1:5]

    # Keyed by provider_code: the BIC is the stable identity, the name is not. The same
    # code appears on both rails under different spellings -- BNORPHMMXXX is
    # "BDO Unibank, Inc." on InstaPay and "BANCO DE ORO UNIBANK, INC." on PESONet.
    by_code = {}
    for path, rail in ((instapay_path, "instapay"), (pesonet_path, "pesonet")):
        with open(path, encoding="utf8") as fh:
            payload = json.load(fh)

        count = 0
        for bank_id, name, code in records(payload):
            entry = by_code.setdefault(code, {"code": code, "names": {}, "rails": set(), "id": bank_id})
            entry["names"][rail] = name
            entry["rails"].add(rail)
            count += 1

        print(f"  {rail}: {count} institutions")

    # A few banks are listed once per rail under DIFFERENT codes -- PNB is PNBMPHMMTOD on
    # InstaPay and PNBMPHMMXXX on PESONet -- and sending the wrong rail's BIC fails. Codes
    # sharing an 8-char prefix are the same institution only when their rails are disjoint;
    # overlapping rails mean genuinely separate institutions that happen to share a prefix
    # ("Security Bank Corporation" and "Security Bank Corporation 2" are both on InstaPay).
    groups = []
    by_prefix = {}
    for code, entry in by_code.items():
        siblings = by_prefix.setdefault(code[:8], [])
        mergeable = next((s for s in siblings if not (s["rails"] & entry["rails"])), None)
        if mergeable:
            mergeable["members"].append(entry)
            mergeable["rails"] |= entry["rails"]
        else:
            group = {"prefix": code[:8], "members": [entry], "rails": set(entry["rails"])}
            siblings.append(group)
            groups.append(group)

    # A legacy code belongs to exactly one institution. Where two share a prefix
    # ("Security Bank Corporation" and "... 2" are both SETCPHMM), the one reachable on
    # more rails is the canonical bank those saved records meant; the other is slugged.
    legacy_owner = {}
    for group in groups:
        if group["prefix"] in LEGACY_CODES:
            incumbent = legacy_owner.get(group["prefix"])
            if incumbent is None or len(group["rails"]) > len(incumbent["rails"]):
                legacy_owner[group["prefix"]] = group

    banks = []
    for group in groups:
        rail_bic = {rail: m["code"] for m in group["members"] for rail in m["rails"]}
        # InstaPay names are mixed-case and readable; PESONet returns shouty legal names.
        legal = next((m["names"][r] for m in group["members"] for r in ("instapay", "pesonet")
                      if r in m["names"]), "")
        display = DISPLAY_NAMES.get(group["prefix"], legal)

        banks.append({
            "code": (LEGACY_CODES[group["prefix"]]
                     if legacy_owner.get(group["prefix"]) is group else slug(display)),
            "name": display,
            # Kept when it differs, so support can tie a row back to PayMongo's own naming.
            "legalName": None if display == legal else legal,
            # Default for callers that do not name a rail. InstaPay first: it is the rail
            # almost every driver payout takes (real-time, under PHP 50,000).
            "bic": rail_bic.get("instapay") or rail_bic.get("pesonet"),
            "instapayBic": rail_bic.get("instapay"),
            "pesonetBic": rail_bic.get("pesonet"),
            "bankId": group["members"][0]["id"],
            "instapay": "instapay" in group["rails"],
            "pesonet": "pesonet" in group["rails"],
            "type": "ewallet" if any(h in legal.lower() for h in EWALLET_HINTS) else "bank",
        })

    banks.sort(key=lambda b: b["name"].casefold())

    split = [b["name"] for b in banks
             if b["instapayBic"] and b["pesonetBic"] and b["instapayBic"] != b["pesonetBic"]]
    if split:
        print(f"  per-rail BICs differ for: {', '.join(split)}")

    codes = [b["code"] for b in banks]
    dupes = sorted({c for c in codes if codes.count(c) > 1})
    if dupes:
        # Distinct institutions that slugged to the same code; one would be unreachable.
        # A legacy code holder keeps the short form, the other gets its BIC appended.
        legacy = set(LEGACY_CODES.values())
        for bank in banks:
            if bank["code"] in dupes and bank["code"] not in legacy:
                bank["code"] = f"{bank['code']}_{bank['bic'][:8]}"
        print(f"  disambiguated: {', '.join(dupes)}")

    codes = [b["code"] for b in banks]
    still = sorted({c for c in codes if codes.count(c) > 1})
    if still:
        raise SystemExit(f"codes still ambiguous: {still}")

    resolved = set(codes)
    lost = [c for c in LEGACY_CODES.values() if c not in resolved]
    if lost:
        raise SystemExit(
            f"legacy codes no longer resolve: {lost}. Saved withdrawal methods using them "
            "would break -- reconcile LEGACY_CODES against the live list before writing."
        )

    with open(out_path, "w", encoding="utf8") as fh:
        json.dump({"_source": source_url, "_generated": "fetch-paymongo-banks.sh", "banks": banks},
                  fh, indent=2, ensure_ascii=False)
        fh.write("\n")

    ewallets = sum(1 for b in banks if b["type"] == "ewallet")
    print(f"  merged: {len(banks)} institutions ({ewallets} e-wallets), all with a BIC")


if __name__ == "__main__":
    main()
