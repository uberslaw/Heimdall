# Planned improvements

Parked work from the Machines level-1 redesign and related fleet UX. Not scheduled for the current pass.

## Machine detail (level 2)

- Edit **Friendly name** and **Team** on the machine page
- Show **Active app list** (what’s tracked) and **Ignored app list**; edit from that page
- **Last check-in** display: 1–59m, then 24h `HH:MM`, or previous-day `DD/MM/YY`
- **Type** (RDP / Local), open sessions
- Move Analyze / Socratize / pending-approval actions off the list onto this page

## Level 3 drill-down

- Machine → Resource usage → dated raw samples / per-app share of hardware
- Same hierarchy for sessions and app usage over any range with stored data

## Fleet-wide sampling

- Always-on ~30s sampling for all Machines-list hosts (not only Historical Dashboard enrollment)
- So **Passive** use and **GPU/CPU hours + Dr/Dw/NTx/NRx** fill for every machine over selected windows

## Idle status (full rules)

- Idle = disconnected ≥15m **and** CPU/GPU &lt;10% each **and** no moderate disk/network
- Requires continuous samples + tuned thresholds

## Baseline page (under Machines)

- Select machines and a recording period
- Sample overall hardware usage (~30s, tunable) across all programs
- Export/upload for analysis to set Idle / Passive thresholds (SOE and Windows overnight noise)

## Min-busy gate

- Exclude Core Windows and SOE security processes when deciding “busy” / Passive

## DT (Desktop Team / named accounts)

- Named accounts in a **DT group**; their active session time (RDP + Local; not disconnected) apportioned out of major fleet stats
- Separate **DT page** showing time spent on machines

## Business hours utilisation

- Toggle **core business hours** (08:30–17:00)
- Show % use in office hours vs outside

## Fleet overview pages

- High-level views: machines & status, resource & app usage, sessions
- Filter by team; click through to machine detail then level 3

## New-machine app tracking

- Track which apps a new machine uses
- Ignore SOE and Windows Core processes

## Admin settings → Data and Retention

- Show breakdown of what is captured on clients
- How long each data point is kept
- What gets transmitted to the database and how often (if ever)
- What is cleaned up or compacted
- Consider DB size alerts
- Show how much data is stored on each client
- Ability to trim/purge old data remotely
- Show current DB size and expected growth based on current input trends

## Capture settings fine-tuning

- Increase/decrease sampling frequency
- Global and Team level set via Data and Retention page
- Show projected growth of local and DB storage based on new settings **before** applying them
- Allow time-based settings: capture more data for X days/weeks/months then revert to default
- Individual machine page: local-level tuning with the same functionality
