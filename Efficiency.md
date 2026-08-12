# Efficiency.md

Living notes for site efficiency / Fleet workflow tightening. Crash recovery, not polished docs.

## 2026-08-12 22:56 AEST — start

- Plan: Fleet-first chunks A–D. Do not edit the plan file.

## 22:57 — Chunk A chrome

- Shared `IndexModel.RangeFromPeriod` / `RangeFromStatsDays` / `FormatLocalTimestamp` (dd/MM/yyyy - HH:mm).
- `_Toasts.cshtml` + `HeimdallTable.initToasts`.
- Fleet header purpose line per tab; toasts instead of alerts.
- Computers / Live / Sessions / Client version / Cost / Stats: period carry-through on host/user/app links.
- Live user link was hardcoded `range=7d` → `1d` (today).
- Buttons: Client version / Sessions / Cost Edit → `hd-btn-gold` / `hd-btn-silver`.

## 22:58 — Chunk B load

- Computers: no longer `Sessions.ToList()` of the whole table. Load by `MachineId` with a light projection; DateTimeOffset freshness in memory.
- `ops-tabs.js`: cache keyed by **src** (not tab name); GET forms in the pane fetch the partial and `replaceState` Fleet query (period Apply no longer reloads layout).
- Filter forms: `asp-page` set on Index / Sessions / Stats / Cost so action is not `/Fleet`.

## 22:59 — Chunk C IA

- Sessions hostname → Machine page; intro copy updated (checkbox + Apply still opens the log).
- Stats usernames → `/User`, process names → `/Application` with mapped range.
- Cost hostname → Machine (`4w` ≈ 30d hours); Finance handoff in subtitle.
- Online / Client version: purpose copy; machine links carry `range=7d`.
- Computers vs Live vs Online already use different status languages (session / TUFLOW busy / ping). Left columns in place.

## 23:00 — Chunk D admin

- Config + Utilization: toasts; Config links App lists + Discovery.
- Discovery: `partial=1` + `#discovery-swap` wrapper for a later filter fetch. Checkbox still GET (classify script is one IIFE — hoisting bind parked).
- App lists: Fleet-style AJAX tabs (`ops-tabs.js`); GET shell skips heavy `LoadAsync`; POST validation still SSRs the tab. Dropped duplicate Discovery/Usage header buttons (nav already has them). Changelog kept.

## Parked

- Discovery filter fetch without full reload (need `HeimdallDiscovery.bind()` hoist).
- Machine disk-scan `location.reload()` on complete (results need the result tree; poll already JSON).
- Config include/exclude vs App lists data-model merge.
- `btn-ghost` leftovers on Config process-list chrome.
- Flood / TUFLOW restyle.
