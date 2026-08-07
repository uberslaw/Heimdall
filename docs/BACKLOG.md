# Heimdall backlog

Parked product ideas so names and intent are not lost.

## Socratize (shipped POC — keep the name)

**Socratize** = retrospective interrogation of *already collected* Heimdall data for **one machine**: a Socratic cost-justification brief (users/teams, local vs RDP, util %, RDP disconnected waste, dominant apps, MetricPolicy in scope, heuristic POC verdict).

Entry: Fleet → All computers row action / select → `/Socratize?host=HOSTNAME` (default last 30 days).

Keep the product name **Socratize** for this machine deep-dive.

### Future Socratize ideas

- Deeper usage patterns (hour-of-day, bursty vs steady)
- Compare to peer machines in the same office / group
- Export PDF / shareable brief for managers
- Stronger chargeback narrative vs CADFX-style reports

---

## Related (shipped — do not confuse with Flight Recorder)

**Always-on fleet sampling** (~30s for every known Machine) feeds **Fleet → All computers** util columns, **Fleet → Live**, and Flood analytics. Top processes on each sample power metric drill-down. **Flood hub** (`/Flood`) keeps Flood-allowlist Live / Historical / Enrollment + Fleet Sims for TUFLOW; enrollment does **not** gate util sampling.

That is continuous fleet telemetry, not incident ring-buffer capture.

Keep Flight Recorder as the *separate* future “what happened when tuflow choked?” arm below.

---

## Socratize → Flight Recorder / Deep Observe (named, not built)

**Not the same as retrospective Socratize**, and **not the same as always-on fleet sampling / Flood hub**. Optional high-cardinality capture *while a watched process runs* (or on demand), later fed to AI / Socratize for incident questions.

### Motivating example

`tuflow.exe` (and similar modelling tools) can be sensitive to **network dropouts**. Managers/engineers want plugins/scripts that, while the app runs (or around incidents), log rich contextual machine/app/network data to upload later for AI pattern analysis — “what was happening when tuflow choked?”

### Intended shape

- Allowlist watched processes (e.g. `tuflow.exe`)
- Sample around the process: network / RDP / disk / CPU (and related signals)
- Agent-side **ring buffer** uploaded as `FlightRecording` blobs
- Correlate process `SessionId` + NIC errors + RDP disconnects
- Feed recordings into AI analysis and/or a future Socratize “incident” mode

### Candidate tech (later)

- ETW: `TcpIp` / `Microsoft-Windows-Kernel-Network` (prefer over full ProcMon)
- Performance counters
- `Get-NetTCPConnection` (or equivalent) sampling
- **Avoid** defaulting to WFP / full packet capture (too heavy for fleet agents)

UI teaser lives on the Socratize page as **Flight Recorder (coming)**.
