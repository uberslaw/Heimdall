# Specialization team review & Fleet Apps — requirements

Durable capture of product decisions (so reconnects don’t lose chat input).  
Status: **requirements agreed for design**; implementation not started in this doc pass.

Related UI references: spreadsheet columns were *Application and exe / Found on / Computer Team Name / Track… / Add to Team Software List* — evolved below away from per-machine track Yes/No toward **team-list auto-track + human Continue/Ignore**.

---

## Goals

1. **App-primary** view of newly discovered / newly classified **Specialization** software (path + exe), with hosts secondary.
2. **Auto-add** to the machine’s team **primary** AppList so the team tracks it (other team machines with that list collect stats too).
3. **Weekly human review**: Continue tracking for the team, or move to **ignore** (path + exe).
4. **Presence cleanup**: drop app↔machine when gone locally; if gone everywhere (with network-path exception), remove from AppLists; **flag** 12‑month inactivity.
5. **Agent idle inventory** (CPU + GPU gates) to feed discovery without a forced simultaneous fleet scan.
6. Later: **Fleet Apps** catalogue under Applications (parked in `Planned_Improvements.md`).

---

## Identity

- Track **full executable path + exe name** (same `athena.exe` at a different path = different item).
- AppList / agent include matching for this feature must become path-aware (today entries are process-name only) — accepted design implication.

---

## UI layout (review surface)

**Recommendation (placement):** new page under **Applications** nav (e.g. **Team software review**), not a Discovery tab.

- Discovery & Classify stays classification-only (already dense).
- Review is about **team AppLists / Continue / Ignore**, same product area as future **Fleet Apps**.
- Optional deep-link from Discovery after “Set Spec”.

If product prefers a Discovery tab instead, behaviour below is unchanged — only nav shell differs.

### Row model

- **Collapsed (default):** one row per **path + exe**, with expand chevron (machine list can be long). Team **Continue / Ignore** sit on the **app row / per-team group** (one decision per team — see below), not repeated per machine.
- **Expanded:** **informational only** — Found on host X, team Y, **sorted by team** then hostname. No per-machine Yes/No track buttons.
- Hosts with no `Machine.TeamId` → section **“Machines not in a Team”** (see below).

### Review actions (team grain)

- One decision per **(path + exe, team)**.
- **Continue** — keep on that team’s primary AppList (already auto-added).
- **Ignore** — remove from team tracking for this path+exe and add to **team ignore list** (path + exe). Does not reappear on this review queue.
- If path+exe is **already** on the team’s tracked list, later discoveries on other team machines **do not** re-queue for review.

---

## Auto-track rules

1. New discovery **and/or** new classification as **Specialization** enters the pipeline.
2. Resolve machine → `TeamId` → team’s **primary** AppList link (`TeamAppListLink` marked primary — **new flag**; UI on `/Teams`).
3. **Auto-add** path+exe to that primary list (and ensure list is linked/not excluded for tracking).
4. Surface on review queue as “newly tracked — Continue or Ignore”.
5. **No primary list marked:** hold on review with prompt to pick/mark primary (do not guess among multiple lists).
6. **Machine-based tracking** for this feature is **out of scope** (can be done elsewhere later).

---

## Ignore lists

- **Per team:** path + exe ignored for that team (review + auto-add skip).
- **Per machine:** path + exe ignored for that machine (for back-channel / future machine flows; not the primary review action).
- Recovery UI: Applications back channel — **deferred** to Planned Improvements / later pass.

---

## Machines not in a Team (#7 — detail)

Today `Machine.TeamId` is optional. For Spec apps first seen only on unassigned hosts:

| Topic | Behaviour |
|--------|-----------|
| Auto-add to team list | **No** — there is no team / primary list. |
| Review queue | Show under **“Machines not in a Team”** (informational hosts under the app row). |
| Continue / Ignore (team) | **Disabled** until the machine is assigned a team (or app is handled another way). |
| Optional later | “Ignore on these machines” using **per-machine ignore** list; or prompt “Assign team…” deep-link to machine/Teams. |
| After team assigned | Next inventory/classification pass can auto-add to that team’s primary list and enqueue review **once** (if not already on list / not team-ignored). |

Open product choice (default proposed): unassigned-only apps stay visible for awareness but **do not** block the team review queue; no silent Global list add.

---

## Presence & cleanup

- Maintain **app (path+exe) ↔ machine** presence (explicit link or equivalent derived from inventory + runs).
- When missing from a machine’s inventory / presence (local paths): **remove that machine link**.
- When **no machines** still have it: remove from **application lists** (team lists; confirm system Specialization list separately at implement time).
- **Network sticky:** paths that are **UNC** or **not under `C:\`** (e.g. `P:\…` Tuflow) **remain** linked / in catalog even if temporarily unseen.
- **12‑month flag:** if not seen **active** on any machine for **12 months** (ProcessRuns / last active), **flag** in UI (Fleet Apps / review) — do not silently delete network-sticky items; local items may still follow removal rules above with the flag as a warning.

---

## Agent idle inventory

- Once per week (random slot): if **CPU &lt; 50%** and **GPU &lt; 50%**, run application inventory and report in.
- If not idle: retry **+2 hours**, **max 6** attempts; counter **persists** across sleep / reboot / day boundary.
- After 6 failures: skip until next week’s slot.
- Inventory work throttled to **≤ ~5% CPU**.
- **GPU utilisation sampling** must be **added** (not present today for this gate; many apps are GPU-primary).

---

## Discovery vs this feature

- **Discovery & Classify** unchanged (Core / SOE / Spec classification).
- This review surface is separate (Applications page recommended).
- Existing per-host Analyze → Approve (`Discovered on {host}`) remains for now; not replaced by this team flow.

---

## Primary AppList on Teams

- Add ability on `/Teams` (and `TeamAppListLink`) to mark **one primary** AppList per team.
- Auto-add always targets the **primary** list.
- Enforcement: at most one primary link per team.

---

## Fleet Apps (parked — see Planned_Improvements.md)

Applications → **Fleet Apps**: catalogue of name + location (as Discovery), machine count (clickable) → detail: machines, first detected, last run, run frequency, avg run time, avg hardware resource usage.

---

## Implementation phases (suggested)

1. Schema: path on AppListEntry (or side table), team/machine ignore lists, primary TeamAppListLink, app↔machine presence.
2. Auto-add + review page (Applications) Continue/Ignore.
3. Agent: GPU sample + idle weekly inventory + CPU throttle.
4. Cleanup job + 12‑month inactive flag + network sticky rules.
5. Fleet Apps list + drill-down (Planned Improvements).

---

## Still confirm at implement kickoff (minor)

- Remove-from-lists when unseen everywhere: include **system Specialization** list or only team/user lists?
- Exact “active” signal for 12‑month flag (last ProcessRun vs last inventory sighting).
- Primary list UX copy on Teams page.
