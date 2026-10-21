# Dashboard design

How the DaServer Dashboard looks, moves and is laid out. [prototype.html](prototype.html) is a working
prototype with example data; open it in a browser and use the keys at the bottom.

## Character

Editorial minimalism, quiet luxury. A page of a well-set book, not a control room. It is calm, exact and focused. One
screen does one thing, positions never move, and only the text that changed moves.

The Dashboard is an operator's tool. Every screen answers one question an operator asks, and the next step is always
obvious. What answers a question together sits together: a list and its selected row's detail, the chart and the
logs that explain it, the store beside the clients. Seven screens on a desk hold the whole console.

## Type

| Role | Face | Size |
|---|---|---|
| Display (page title, headline figure, actions) | Shippori Mincho, falling back to Noto Serif JP, Hiragino Mincho, Songti SC, Georgia | Title and folio `clamp(34px, 3.3vw, 46px)` (30 px on phones), headline figure `clamp(96px, 10vw, 140px)`, figures 30–38 px, actions 19–21 px |
| Text (body, tables, labels) | Newsreader, falling back to Iowan Old Style, Palatino, Georgia | Body 17 px (16 px on phones), lead 17–19 px, labels 14 px |
| Code (components, log lines) | IBM Plex Mono | 13.5 px (12 px on phones) |

- Labels are sentence case in the text face at 14 px in the third ink: no all-caps, no letter-spacing.
- The line above a title is italic.
- Tabular figures only in number cells, never in running text.

## Colour

Ink on paper, with one accent from the indigo/blue family. The accent marks whatever is wrong and the action that
fixes it, nothing else. No red, orange or green, no gradients, no tinted panels. Backgrounds are a clean neutral or
slightly cool near-white, and a clean near-black.

Every text tier passes WCAG AA on the paper; the ratios below are against it. The palette is porcelain and kon
(紺, deep indigo).

| Token | Light | Ratio | Dark | Ratio |
|---|---|---|---|---|
| `--paper` | `#F7F8FA` | | `#0E1014` | |
| `--ink` | `#111318` | 17.5 | `#ECEEF2` | 16.4 |
| `--ink-2` | `#3B3F4B` | 9.9 | `#BDC1CB` | 10.6 |
| `--ink-3` | `#5B6070` | 5.9 | `#989DA9` | 7.0 |
| `--indigo` | `#22397E` | 10.1 | `#9CAEF0` | 8.8 |

Hairlines are the ink at 14% opacity (16% in dark), and the stronger header rule is 34%.

## Layout

Every screen is built on one grid, so positions line up from screen to screen and from section to section.

- **Columns.** The measure is 1200 px with 12 columns and 24 px gutters. Columns 1–2 are the margin (folio, what
  comes next, the latest status); columns 3–12 are the page. The header puts the brand over the margin, the sections
  over the page and the environment at the right edge. The footer's key hints start on the page's first column, and its rule spans the 1200 px measure, not the window.
- **Placements on the page's 10 columns.** A screen uses one of four compositions:
  - full: 10 columns, for a chart, a table or a row of figures;
  - main and aside: 6 columns, one empty column, then 3 columns (the incident);
  - halves: 5 and 5 columns, each under a running head (the evidence: the chart and the logs; the platform: the
    store and the clients);
  - list and detail (a master–detail screen): the list on 6 columns and the detail on 4 for server types, whose
    rows compare many columns; the list on 4 and the detail on 6 for changes and actors, whose detail is the larger.
  The second half and the detail start a gutter further in, so the two sides read as two groups without a rule
  between them.
- **Grouping is spacing and running heads, not boxes.** A group starts with its running head (the display face at
  26–32 px, the name of what follows: "The actor store", a server type's name) and a line of context under it;
  groups are set apart by the gutter and by the 24 px between units, never by a frame or a tint.
- **Rhythm.** The vertical unit is 8 px. Spacings are 8, 16, 24, 32 and 40 px, and every table row, property row and
  list row is 44 px (42 px on phones). The body starts 40 px below the title, and units inside it are 32 px apart.
- **Lines that line up.** The folio's number sits on the title's baseline, in the title's size. "Next" starts on the
  body's first line. On a list screen the list's head row is as tall as the detail's running head and its gap, so
  the list's header rule and the detail's first hairline are one line across the page.
- **Centred per section.** Head and body are centred as one block between header and footer. The body row is as tall
  as the tallest screen of the current section, measured, so nothing moves within a section and no section hangs from
  the top over an empty lower half.
- **A screen fits its height.** A list shows as many rows as the height holds, in whole pages, and counts the rest
  beneath it ("4 below"). A detail is made at levels and takes the richest that fits: a server type drops the line
  about its other nodes first, then node rows; an actor drops component and log lines. On a short desk the gaps
  inside a detail close from 24 to 16 px before anything is dropped. The halves (the evidence, the platform) need
  the full measure and height: below 1200 px wide or 800 px high, and on a phone, each half becomes a screen of its
  own (the last hour, the logs; the actor store, the client connections).
- **Narrower desks.** From 720 to 1279 px the margin takes three columns and the page nine, so the contents' names
  read whole; the server type list keeps its name, nodes, p99 and outlier columns there and leaves actors, spread
  and build to the wider desk.
- **One focal point.** Each screen has one place the eye lands first (the incident's figure, the selected list row,
  the chart), then a second (the detail, the logs), then the action.
- **Phones.** One column, in the same order. A list screen is the list alone, its columns cut to the name and the
  reading that matters (a server type's p99 and the nodes that stand out); a tap or Enter opens the row's detail as a
  page of its own, which starts with the columns the list left out, as figures. On that page ↑↓ move to the
  neighbouring rows and Esc returns to the list, on the row last shown. The folio and "Next" move to one line above
  the key hints.

## Components

There is one vocabulary, used the same way everywhere.

- **A unit** is a caption, then a figure, table or chart, then its foot. In the foot, actions come first and the
  history link ("Open in Grafana") sits at the end, on the unit's right edge. An action sits where the decision is
  made: Restart on a node's row, Hot restart in the foot of the type's detail, a rollback in the rollout's
  detail. The foot follows its unit at the body's rhythm, never pinned to the bottom of the reserved height: the space
  a section reserves for its tallest screen lies below the last unit, not between a unit and its foot, and the body
  keeps 24 px clear above the footer rule. A foot's action is set like the foot (15 px, its own baseline, a 1 px
  underline), not in the larger face of a page action. A table lists only the rows it has: a quiet server type has no
  node table, and its foot follows the distribution.
- **A master list** is a table whose rows are the items of a list screen, numbered at the left in ink-3 so a number
  key picks them. Every row is in the second ink and the selected one is in ink with its name in weight 600: one
  indicator, no highlight band. The accent still marks what is wrong in any row. A row is a step: ↑↓, the wheel, a
  click or its number select it, and the detail beside it morphs.
- **A spread** is a distribution as a small multiple, 18 px tall in a list row: 32 bins of 2 ms from 0 to 64 ms,
  the same scale on every row and the limit dashed, so ten server types compare at a glance (Tufte's small
  multiples). Each row's bars are scaled to its own fullest bin. The detail carries the full distribution.
- **A figure** is a label above, the reading in the display face, and its normal below ("usually 12 ms",
  "of 4 nodes"). A reading is never shown alone.
- **A distribution** shows one reading across every node of a server type: 80 bins of thin bars in the ink, the bins
  past the limit or holding an outlier in the accent, the limit as a dashed line, and the scale (zero, the limit, the
  top) beneath. It reads the same at 4 nodes and at 400. Moving between server types, each bar grows or shrinks from
  its foot instead of being redrawn.
- **A table** has one entity per table, one row height and hairlines that run unbroken. Text columns are left
  aligned, and numbers are right aligned in tabular figures. The gutter is padding on the side away from the
  alignment.
- **Actions.**
  - The primary action is in the accent.
  - The secondary action is in the second ink.
  - Both are words in the display face with a rule beneath them.
  - A row action (Restart) and a history link are the small text action.
- **States.**
  - Hover darkens the ink one step.
  - Pressing an action nudges it down 1 px.
  - Keyboard focus is a 1 px accent outline.
  - The selected tab, list row or Find result is shown by ink weight and darkness only.
  - A pressed key underlines its hint at once, before anything moves.

## Structure

- Hairlines only. There are no cards, boxes, panels, badges, pills, round icons or shadows, and every corner is
  square.
- One table per entity. Rows have the same height, and columns never mix kinds.
- What is wrong is in the accent. When nothing is wrong, nothing is in the accent.

## Focus

- One screen, one question. What answers it together is on one screen; a part that does not fit is cut by level
  (rows, lines), and only the halves split, on a short or narrow desk. Nothing is shrunk to fit and the page never
  scrolls.
- Fixed positions. Header, tabs, margin and key hints never move. The title stays put within a section, and the
  "Next" line keeps its height when there is nothing next.
- Nothing appears twice. Each fact has one home, and other screens link to it.
- Cut what does not answer the screen's question. If a sentence restates the table under it, or the eyebrow repeats
  the title, it goes.

## Navigation

| Input | Does |
|---|---|
| ↓ / ↑, wheel, vertical swipe, PageDown / PageUp, j / k | The next or previous step within the current section. On a list screen a step is a row: the selection moves and the detail morphs; past the last row the next screen opens. It stops at the section's first and last steps and never crosses into another section |
| → / ←, horizontal swipe, a section in the edge index | The next or previous section, opening at its first step |
| A row of a list | Selects it on a desk; on a phone (or with Enter) opens its detail as a page, where ↑↓ move between rows |
| 1–9 (two digits in a row for 10 and on) | On a list screen, that row (the number printed beside it); elsewhere, that screen of the section |
| Home / End | The section's first or last step |
| A contents entry | That screen; a list opens on the row it was left on |
| A link in a detail ("Open MapServer in the cluster") | Opens its target on its own screen; Esc returns |
| `/` or ⌘K / Ctrl+K | Find: actors by id, server types, nodes, screens by name, actions; one column, ranked as you type; with nothing typed, the current section's other screens come first; ↑↓ select, Enter opens |
| Esc or Backspace | Back. It closes Find, a confirmation or a row's page, otherwise returns from the last jump (a link, a Find result). It is dimmed while there is nowhere to go back to |

- **Sections: an edge index.** There is no menu bar. On a desk the five sections stand in a short column at the
  window's left edge, like a reference book's printed thumb index but set horizontally so each name reads at a
  glance: 14 px, ink-3, vertically centred, 22 px from the edge; the current section is in ink and its 4 px ink mark
  bleeds off the edge. That mark is the only selected-state indicator. Below 1440 px the page moves right to leave the
  index its own space, so nothing ever overlaps it. On a
  phone the index moves to the bottom edge, under the thumb: one line above the key hints, the current section in
  ink with a 1 px ink segment on the footer rule. The header is a running head only (the brand at the left, the
  environment at the right) with no rule under it. The index never moves.
- **Screens.** Seven on a desk:

  | Section | Screens |
  |---|---|
  | Incident | Summary (the figure, the cause, the fix; the distribution and the worst nodes beside it), Evidence (the hour's chart and the log patterns as halves) |
  | Cluster | Server types (master–detail: every type as a row, the selected type's distribution, the nodes that stand out and its actions), Platform (the actor store and the client connections as halves) |
  | Changes | Changes (master–detail: the hour's changes, the selected one's detail, its rollback and a link to its target) |
  | Actors | Actors (master–detail: the slowest actors and any looked up, the selected one's state, components and logs) |
  | Audit | Audit log |

  On a desk below 1200 × 800 the halves split, so Incident and Cluster hold three screens each; a phone has the
  same screens, and a row's detail is a page of its own.
- **Folio and contents.** The folio counts what ↑↓ move through on this screen: the selected row of a list ("3 of 10
  server types", "2 of 6 changes"), otherwise the screen of the section ("2 of 2"). In the margin, under the folio,
  the section's screens are listed by number and name, 15 px, ink-3, with the current one in ink and a screen holding
  something wrong in the accent. The rows of a list are on the page, not in the margin, so the contents stay a few
  lines long. A click opens a screen. A section of one screen (Changes, Actors, Audit) shows its folio only, since a
  list of one would repeat the title. On a phone the folio is a link that opens the contents as a page of its own;
  ↑↓ move, Enter opens, Esc closes.
- **Find** is one column in the main measure. The input carries the short placeholder "Actor, server type, node or
  action". Results are rows of 44 px, the name at the left and its context at the right, never wrapping; groups are
  small running heads. Find's height is set by the rows the window holds (four to nine on a desk), so the head stays
  put while results narrow, and a final line counts what is not shown ("405 more; keep typing"). Find's results are a
  list, not pages: the wheel moves through them freely, one row per notch or per 40 px of a trackpad, and the
  one-scroll-one-step rule applies to screens and list rows only. The phone's contents page moves the same way.
- **One scroll, one step.** A run of wheel events is one scroll however long it lasts: a spin of many notches, an
  accelerating spin, or a flick with its tail. A new scroll starts after 240 ms of quiet, with a turn the other way,
  or with a clear new flick on a decayed tail: the samples have fallen below a quarter of the scroll's peak, and the
  new sample is at least 30 px and three times the smallest of the last five. A notch steps on its first event, a
  pixel stream after 24 px, and a scroll that comes during the 450 ms transition steps when it ends. Checked in a
  browser against the events browsers send, an accelerating macOS spin and a jittery trackpad tail included.
- Every key hint at the bottom is bold, always in the same place, and clickable. A pressed key underlines its hint
  at once, so the key is answered before anything has moved. A key that only one screen uses
  (such as rolling back) is a button on that screen, not a global key.
- Every action that changes the cluster asks you to type the target's name, and it is written to the audit log.

## Motion

Two motions, and nothing else: no fades or eases on whole blocks, no scrambled text, no wipes.

**Text morph (Magic Move by character).** When a screen keeps its layout and only its values change (the next server
type, the next list item, a status line, the folio), every text changes by character, like Keynote's Magic Move with
matching set to characters. It combines three techniques:

1. **Which characters stay.** The old and new strings are compared by character with a longest common subsequence
   (LCS), the same idea as `git diff`. A kept run must be at least two characters long unless it starts or ends both
   strings, so a lone shared letter does not fly across a word.
2. **Kept characters glide (FLIP).** First, record where each character is drawn. Last, change the text and measure
   again. Invert, by moving each kept character back to its old place. Play, by animating it to the new place. Only
   the transform animates, so layout never moves. A size or colour difference animates with it.
3. **New and old characters.**
   - A new character blurs in: it starts at 2 px blur and opacity 0, slightly low, and settles, without delay, so a
     changed digit is never a visible gap.
   - A removed character blurs out where it stood: it blurs, rises slightly and fades, faster than the new one
     arrives.

How it is drawn:

- The motion happens on a fixed layer of one-character clones, in each character's own font and colour.
- The real text sits in its final place in transparent ink, so table hairlines stay.
- When the morph ends, the clones are removed and the text shows exactly where they stopped (checked to 0 px).
- A morph that starts mid-flight starts from where the clones are now.
- Text longer than 160 characters (a log line) changes whole.

**Directional glide.** A new page (another layout, another section, Find, a confirmation) enters from the direction of
travel, block by block with a staggered delay:

- Down goes to the next screen.
- Up goes to the previous one.
- Left and right change section.

The leaving page exits quickly the other way. The title, eyebrow and margin texts morph across the change.

| Motion | Timing | Easing |
|---|---|---|
| Kept characters (FLIP) | 420 ms | `cubic-bezier(.2, .7, .2, 1)` for all motion |
| Characters blurring in | 230 ms, no delay | the same |
| Characters blurring out | 190 ms | the same |
| New page, per block | 360 ms, 18 px vertical or 28 px horizontal, first block at 60 ms, then 35 ms apart | the same |
| Leaving page | 160 ms, half the distance, the opposite way | `cubic-bezier(.4, 0, 1, 1)` |
| Reduced motion | Text and pages change in place | none |

The morph is our own, about a hundred lines of code. Libraries doing the same family of effect are
[NumberFlow](https://number-flow.barvian.me/), for numbers, and [Torph](https://github.com/lochie/torph), for text.
Torph matches whole words when the text has spaces, keeps one instance per element and lays characters out as
inline-block. We match by character across re-rendered pages and need the layout untouched.

## At scale

A cluster has tens of server types, hundreds of nodes per type and millions of actors. No screen grows with them.

- **Distributions, not rows.** A reading across a type's nodes is one distribution. The table under it lists only
  the nodes that stand out: a state other than running, or a reading more than four median absolute deviations from
  the type's median. At most four rows; the rest are one line with their count and range. When no node stands out
  there is no table, and the line says "All 32 nodes within their usual range".
- **Unhealthy first, every type a row.** Server types are one list, the unhealthy first, then by size. Tens of
  types fit as whole pages of rows with the rest counted beneath; each row carries its spread, so the list compares
  the types and the detail explains the selected one.
- **Top-N, then Find.** Actors appear as the slowest six across the cluster; any other actor is found by its id.
  Find shows five results per group and says how many more match.
- **Counts read at a glance.** Large counts are rounded (2.41 M, 412 k); exact figures stay where they are small. A count
  column holds numbers only: zero is 0, never "None".
- **Logs by pattern.** Hundreds of nodes log the same line thousands of times; Loki groups them by pattern, and the
  screen shows the patterns with their counts.
- **Where it is computed.** Distributions, outliers and rankings are computed by the Dashboard's server once per
  refresh, never in the browser and never per viewer (ADR-093). The browser receives the bins and the few rows it
  shows. Nodes keep no history: charts come from Prometheus's range queries, or, without Prometheus, from the
  Dashboard's own ring of polls, which starts when the Dashboard starts (the chart says so).
- **Alerts decide, outliers point.** Whether something is wrong is Prometheus's alert rules
  (`observability/prometheus-rules.yaml`); the outlier rule only names the culprit nodes and the rows that stand
  out. Without Prometheus the Incident section covers what needs no threshold: a stalled shard, a node not ready,
  views that disagree.

## What each job looks like

DaServer is a general framework, so every view below names server types, actors and nodes rather than any game.

| Job | The operator's question | Presentation | Left out |
|---|---|---|---|
| Triage | Is anything wrong right now? | The Incident section's first screen, one per firing alert: what is wrong as the title; the figure that proves it beside its limit and usual value, one sentence of cause and the fix as the primary action (main); the distribution of the reading across the affected type's nodes and its three worst nodes (aside), which tells a bad build (every node moved) from a bad node (one bar apart). When nothing is wrong, the title says so and the section has one screen | Status grids, counts of healthy things, coloured dots |
| Evidence | Why do we think so? | The Incident section's second screen, two halves: the last hour as one chart (the culprit in the accent, the slowest of the others in ink, the limit dashed, the change that caused it marked), and beside it the logs, read from Loki and grouped by pattern (a pattern and its lines; on a full-width screen also how many nodes logged it and when last) | Dashboards of every metric; raw lines from hundreds of nodes; history beyond the hour (one link to Grafana) |
| Cluster health | Which server type is unhealthy? | The list of Server types: one row per type (nodes, actors, ask p99, its spread across the nodes, how many stand out, build), unhealthy types first, the unhealthy row's name and p99 in the accent; membership is the eyebrow | Per-node detail in the list; a status column |
| A server type | How are its nodes? | The detail beside the list: the type's name as the running head, the distribution of ask p99 across all its nodes with its usual value, the nodes that stand out (at most four rows: name, p99, and Restart or what the node is doing, "Leaving, 35 of 64 moved"), one line for the rest ("396 other nodes between 34 and 49 ms"); Hot restart and Grafana in the foot. Selecting another type morphs the detail and grows or shrinks each bar from its foot | A row per node; sparklines that are flat; graphs per node |
| Membership | Do all nodes agree on who is in the cluster? | One phrase above the overview title ("630 nodes in 10 server types, every view agrees"); a disagreement becomes the incident | A separate membership page while views agree |
| Store | Are actors being saved? | The Platform screen's first half: write p99, writes in flight and unsaved actors with their usual values (errors in the line under the running head while there are none); the actor types with the most unsaved actors and their oldest (as many of four rows as fit) | Query plans, per-table statistics |
| Client connections | Are clients connected and balanced? | The Platform screen's second half: connected, the average per gateway, resumed (refused handshakes in the line under the running head while there are none); the distribution of clients across gateways; the gateways more than a tenth from the average (at most three rows) | A row per gateway; per-client lists |
| Changes | What changed before it broke? | One master–detail screen: the last hour's changes, newest first, as the list (time, kind, target; the bad one in the accent); the selected change's detail beside it (what, who, the result, the finding), a bad rollout's roll back, and a link to its target in the cluster | Deploy logs; the full rollout timeline |
| Rollout and rollback | Roll back the bad build | The primary action on the incident and on the rollout's detail, confirmed by typing the server type's name. A new build, the previous one included, reaches the nodes through the deploy (ADR-093): the Dashboard calls the deploy pipeline's webhook with the server type and the build to return to, and audits the call; without a webhook it shows the command to run | A separate deploy page |
| Restart, hot restart | Restart a node; restart a type on its build | Restart on the node's row (`/admin/restart?exit=true` on that node: it hot restarts in place, exits after its snapshot and is started again by the platform); Hot restart under the node table (a same-image roll of the type: the Dashboard calls the deploy pipeline's webhook, which runs the one rollout loop, one node at a time and the coordinator last, with scale-in paused; without a webhook it shows the command, `aspire do {cluster}-restart`); both confirmed by typing the name. The Dashboard never loops over nodes itself: a restart of the Dashboard would leave a half-rolled group, and only the loop pauses scale-in. Taking a node out for good is the platform's scale-down | Bulk selection |
| Config tables | Push a new table | An action in Find, confirmed by typing the table's name; accepted only when every node accepts it | An editor |
| An actor | What state is this actor in? | One master–detail screen: the six slowest actors in the cluster as the list (with any found by its id through Find), the selected actor beside it in full: handler p99, owning node, requests, mailbox, type, last saved, its components and its last log lines (from Loki), with a link to its traces (Grafana Explore over Tempo) | Lists of all actors; raw JSON dumps; live editing |
| Audit | Who did what? | One row per action: time, who, action, target, newest first, paged; the audit is the Dashboard's log stream, read from Loki | Filters and search; export (the audit log is in the logs too) |
| History | What happened last week? | A link on each view to Grafana at the same time range (the shipped dashboard, or Explore for logs and traces) | Our own long-term storage, ad-hoc queries, custom dashboards |

## What we deliberately do not do

- No ad-hoc queries and no dashboard editor. Long-term history stays in Prometheus, Loki and Tempo, read through
  Grafana, and every view links there.
- No thresholds of our own: alerts are Prometheus's rules.
- No status colours (red, amber, green), no badges, no icons and no avatars.
- No widgets that look busy without answering a question: no gauges, donut charts, flat sparklines or counters of
  healthy things.
- No modal dialogs. Find and confirmations open as pages, and Esc goes back.
- No scrolling pages and no sideways scrolling, on phones included.
