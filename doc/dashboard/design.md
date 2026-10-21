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
- **A spread** is a distribution as a small multiple, 18 px tall in a list row: 32 bins on a square-root scale from
  0 to 64 ms, the same scale on every row and the limit dashed, so ten server types compare at a glance (Tufte's
  small multiples). The last bin holds everything from 64 ms on; the row's p99 column says how far. Each row's bars
  are scaled to its own fullest bin. The detail carries the full distribution.
- **A figure** is a label above, the reading in the display face, and its normal below ("usually 12 ms",
  "of 4 nodes"). A reading is never shown alone.
- **A distribution** shows one reading across every node of a server type that answered with a fresh reading: 80
  bins of thin bars in the ink, the bins past the limit or holding an outlier in the accent and marked by a short
  rule beneath the axis, the limit as a dashed line, and the scale (zero, the limit, the top, on a 1, 2, 2.5, 4, 5
  progression) beneath. Its caption counts what it holds ("397 of 400 nodes answered"). It reads the same at 4 nodes
  and at 400. Moving between server types, each bar grows or shrinks from its foot instead of being redrawn.
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
- What is wrong is in the accent. When nothing is wrong, nothing is in the accent. Colour is never the only sign:
  in a table what is wrong is also italic, a distribution marks its bins beneath the axis, and a count ("Out",
  "3 of 400") says it in words.
- "Cannot know" is not "fine". A source that cannot be read (Prometheus, Alertmanager, a node) is said in the accent,
  never shown as silence.

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
- Every action that changes the cluster asks you to type the target's name, and it is written to the audit log
  (see Actions).

## Live data

The page is a view of the Dashboard server's latest snapshot, refreshed every cadence (10 s by default).

- **When.** The margin's status line gives the snapshot's time and how many nodes gave a fresh reading ("As of
  18:02:10. 627 of 630 nodes read fresh"): a node that answers with an isolated or stale reading is not counted, so a
  cluster frozen by a store outage never reads "630 of 630". The environment in the header names the time zone every
  time is in, UTC unless configured; a configured zone shows its offset ("18:04 UTC+2"), so a time stays unambiguous
  across a daylight-saving change, and every time sent to Alertmanager or the deploy pipeline is UTC. Every age ("14 s
  ago") is counted on the snapshot's clock, never a node's.
- **Freshness.** Each node's reading carries its age. A node that does not answer is "Not answering, 41 s"; a node
  isolated from the store (it fails readiness, answers probes as isolated and exits at its bound) is "Isolated, exits by
  18:04"; a reading older than two cadences is "Stale". None of them enters a distribution, and the outlier rule lists
  them first.
- **The poll always runs.** Even with Prometheus, the Dashboard polls every node's `/health` and summary itself:
  liveness, isolation, build and incarnation come only from the nodes, and Prometheus fed by push cannot tell a dead
  node from a quiet one. Each request has a 2 s timeout. A node that failed its last poll is asked again only every
  fourth cadence until it answers, so a lost zone does not spend the cadence on timeouts; the number of requests in
  flight is sized from the cluster (nodes ÷ (cadence ÷ timeout), at least 32), so a full poll fits in a cadence at
  2,000 nodes. Each Dashboard replica polls on its own; the poll is light (one small summary per node).
- **A refresh patches, it does not re-render.** Texts that changed swap in place and bars take their new heights.
  Keyboard focus, the pointer's row, and anything being typed (a confirmation, Find) stay as they are; Find and a
  confirmation are never re-rendered by a refresh.
- **Orders hold.** A list keeps the order the operator sees until they move to another section; selection is kept by
  id, not position, and a number key acts on the order as it was last painted. A refresh that would add or drop rows
  still updates every value that kept its row, and the status line says "Rows changed; move away to see them"; the
  rows themselves change once the pointer and focus leave the page, or after 30 s at the latest, so values are never
  frozen under a fresh time.
- **Announcements.** Only the status line is a live region, and only its messages are announced, not each refresh.

## Motion

Two motions, and nothing else: no fades or eases on whole blocks, no scrambled text, no wipes.

**Text morph (Magic Move by character).** When the operator moves and a screen keeps its layout while only its values
change (the next server type, the next list item, a status line, the folio), every text changes by character, like
Keynote's Magic Move with matching set to characters. A data refresh never morphs: a value that changed swaps in
place, so a figure is never unreadable while the operator reads it. It combines three techniques:

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
  the nodes that stand out, in this order: no fresh reading (not answering, isolated, stale); a state other than
  running (leaving, joining, restarting, "Requests swapped since takeover"); no requests while the type has them; a reading more than four median absolute deviations from the median
  of the nodes on the same build, either way (the deviation has a floor of 2% of the median, so a type whose nodes
  read alike does not flag noise, and an absolute floor of 0.5 ms or one item, so a median of 0 does not flag every
  nonzero reading). A build with fewer than five fresh nodes, such as a canary, is compared with the majority build's
  median and deviation instead of its own, since a node cannot stand out from itself. At most four rows; the rest are one line with their count and range, and if a node
  that Find chose took a row, how many more stand out. When no node stands out there is no table, and the line says
  "All 32 nodes that answered within their usual range".
- **Builds as counts.** A type halfway through a rollout runs two builds: its row says "2 builds", its detail gives the
  counts ("388 on 20-3, 12 on 20-2"), outliers are found within each build, and the incident's cause compares the
  builds' readings instead of claiming "every node moved". What is rolling and how far comes from the builds the nodes
  report in membership, never from the page.
- **Unhealthy first, every type a row.** Server types are one list, the unhealthy (an alert fires for it) first, then
  by size. Tens of
  types fit as whole pages of rows with the rest counted beneath; each row carries its spread, so the list compares
  the types and the detail explains the selected one.
- **Top-N, then Find.** Actors appear as the slowest six across the cluster, merged from each node's own ranking: an
  actor needs 100 calls in the window to rank, and one seen on two nodes during a handoff counts once. Any other actor
  is found by its id: the key is parsed by its type's registered key type, looked up on Enter or after a pause in
  typing, and asked of its owner; a lookup never loads an actor, and one that is not loaded says so. Find shows five
  results per group and says how many more match.
- **Counts read at a glance.** Large counts are rounded (412 k, 2.41 M, 1.20 G), exact below 100,000 and grouped
  (9,412). Durations switch to seconds at a second (1.24 s). A count column holds numbers only: zero is 0, never
  "None"; a missing reading is a dash, never 0 or NaN.
- **Logs by pattern.** Hundreds of nodes log the same line thousands of times; Loki's patterns API (the pattern
  ingester) groups them, and the screen shows the patterns with their counts. A log query is one per server type with
  the node as a label, never one per node (a type of 630 nodes would pass Loki's series limit), and its result is
  cached in the snapshot, so ten viewers cost Loki one query. Patterns are read each cadence only for the incident's
  type, and for the other types every five minutes; each read asks only for the minutes since the last one and merges
  them into the hour, never the whole hour again. With 50 types and two replicas that is under one query a second, held
  under Loki's `max_entries_limit_per_query`, `max_query_series` and query timeout.
- **Where it is computed.** Distributions, outliers and rankings are computed by the Dashboard's server once per
  refresh, never in the browser and never per viewer (ADR-093). The browser receives the bins and the few rows it
  shows. A type's p99 is computed from its nodes' histogram buckets merged as deltas (a restarted node's counters
  reset), never as a percentile of the nodes' percentiles. With Prometheus these are three recording rules over the ask
  histogram (`daserver.ask.duration`, in seconds): the buckets' rate summed by cluster, type and `le`
  (`cluster_type:daserver_ask_duration_seconds_bucket:rate1m`), the type's p99 from it
  (`cluster_type:daserver_ask_duration_seconds:p99`, where `histogram_quantile` consumes `le`), and each node's p99
  (`cluster_type_node:daserver_ask_duration_seconds:p99`); the one-minute window holds four pushes.
  Only the node p99 is recorded per node, never the buckets, so the rules
  add about one series per node and two per type: at 2,000 nodes a refresh reads about 2,100 series, cheap even beside
  5 million. Nodes keep no history: charts come from Prometheus's range
  queries, or, without Prometheus, from the Dashboard's own ring of polls, which starts when the Dashboard starts (the
  chart says so).
- **Membership from more than one node.** The Dashboard reads membership from up to three ready nodes, chosen again
  each refresh from the newest view, and keeps the view with the highest version; a node handing over to a standby, or
  leaving, is simply not chosen next time. An actor lookup that meets `NotHosted` asks again on the newer slot table.
  When no node is ready (a store outage isolates every node), the last view stays on the page with its age.
  Disagreement is an incident only when it lasts (the alert's `for: 2m`), since views differ for a moment on every
  join and leave.
- **Alerts decide, outliers point.** Whether something is wrong is Prometheus's alert rules
  (`observability/prometheus-rules.yaml`); the outlier rule only names the culprit nodes and the rows that stand
  out. A type is unhealthy when an alert fires for it. A reading's limit is its rule's `daserver_limit` annotation,
  read from the rules API (`/api/v1/rules`); a reading with no rule has no limit line. Every rule keeps the labels
  `cluster`, `type` and `node`: `type` is the OpenTelemetry resource's `service.name` (the node group) and `node` its
  `service.instance.id`, which DaServer's metrics carry as labels and the rules rename for runtime metrics (Prometheus's
  OTLP endpoint maps them to `job` and `instance`). The metrics take one path: every node pushes over OTLP every 15 s
  (the export interval DaServer pins) to one Collector, which sends them to Prometheus's OTLP endpoint (`/api/v1/otlp`,
  with `otlp.promote_resource_attributes: [daserver.cluster]` in prometheus.yml); Prometheus scrapes no node. Pushed
  series get no staleness markers, so a gauge is read only from its last two minutes (`last_over_time(...[2m])`) and a
  rate over at least four pushes. A node that stops pushing would otherwise read as healthy, since its series fade and
  its alerts resolve; `DaServerNodeSilent` fires for a node seen in the last hour that has pushed nothing for two
  minutes, unless its last push was its graceful stop, and the Dashboard's own poll says "Not answering" regardless. A
  parked hot-restart instance exports no metrics and is never shown as a node: the node is the instance holding the
  lock. Without Prometheus the Incident section covers what needs no threshold: a stalled shard, a
  node not ready, views that disagree.
- **The store down is an incident of its own.** When most nodes are isolated at once, the cause is the store, not the
  nodes: the Incident section's first screen is "The store cannot be reached", with how many nodes are isolated and
  since when, ahead of every alert, and needs no rule. Actions wait for it (see Actions).
- **Incidents, not alerts.** Every rule fires per node, so a bad build fires hundreds of alerts. An incident is the
  alerts of one rule on one server type of one cluster (Alertmanager groups by `alertname`, `cluster` and `type`;
  silences and inhibitions honoured); an alert without a `type` label (a rule over the whole cluster, such as
  `DaServerMembersDisagree`) is an incident of the cluster. Incidents rank by severity (`critical`, then `warning`, as
  the rules name them), then by how many nodes they cover. Each has its screen; the first incident's summary counts the rest
  ("One more incident: long GC pauses on 2 PlayerServer nodes"). A silenced incident stays listed, last, with the
  time its silence ends.
- **When the alerts cannot be read.** If Prometheus or Alertmanager does not answer, its newest sample is older than
  two evaluation intervals, or rule evaluation fails (`prometheus_rule_evaluation_failures_total`), the Incident
  section's title is "Alerts cannot be read", with the time it last could. The newest sample is measured on a series
  every node pushes (`max(timestamp(daserver_cluster_members))`), since Prometheus scrapes no node and has no `up`. The
  state has hysteresis: two failed reads in a row enter it and two good reads leave it, so a slow Prometheus does not
  flip the title every cadence; meanwhile the last incidents read stay listed, marked "as of" their time, the status line says so in the accent, and
  the screen shows the threshold-free checks from the Dashboard's own poll. It never says nothing is wrong. The
  prototype shows this state with `?alerts=down`.

## What each job looks like

DaServer is a general framework, so every view below names server types, actors and nodes rather than any game.

| Job | The operator's question | Presentation | Left out |
|---|---|---|---|
| Triage | Is anything wrong right now? | The Incident section, a screen per incident (the alerts of one rule on one server type), worst first: what is wrong as the title; the figure that proves it beside its rule's limit and its usual value, one sentence of cause read from the builds' readings, the incidents left, and the fix as the primary action with Silence for an hour beside it (main); the distribution of the reading across the affected type's nodes that answered and its three worst nodes (aside), which tells a bad build (every node on it moved) from a bad node (one bar apart). When nothing is wrong, the title says so and the section has one screen; when the alerts cannot be read, the title says that instead; when most nodes are isolated, "The store cannot be reached" comes first | Status grids, counts of healthy things, coloured dots |
| Evidence | Why do we think so? | The Incident section's second screen, two halves: the last hour as one chart (the culprit in the accent, the slowest of the others in ink, the limit dashed, the change that caused it marked), and beside it the logs, read from Loki and grouped by pattern (a pattern and its lines; on a full-width screen also how many nodes logged it and when last) | Dashboards of every metric; raw lines from hundreds of nodes; history beyond the hour (one link to Grafana) |
| Cluster health | Which server type is unhealthy? | The list of Server types: one row per type (nodes, actors, ask p99, its spread across the nodes, how many stand out, build), unhealthy types first, the unhealthy row's name and p99 in the accent; membership is the eyebrow | Per-node detail in the list; a status column |
| A server type | How are its nodes? | The detail beside the list: the type's name as the running head, the distribution of ask p99 across all its nodes with its usual value, the nodes that stand out (at most four rows: name, p99, and Restart or what the node is doing, "Leaving, 35 of 64 moved"), one line for the rest ("396 other nodes between 34 and 49 ms"); Hot restart and Grafana in the foot. Selecting another type morphs the detail and grows or shrinks each bar from its foot | A row per node; sparklines that are flat; graphs per node |
| Membership | Do all nodes agree on who is in the cluster? | One phrase above the overview title ("630 nodes in 10 server types, every view agrees"); a disagreement becomes the incident | A separate membership page while views agree |
| Store | Are actors being saved? | The Platform screen's first half: write p99, writes in flight and unsaved actors with their usual values (errors in the line under the running head while there are none); the actor types with the most unsaved actors and their oldest (as many of four rows as fit) | Query plans, per-table statistics |
| Client connections | Are clients connected and balanced? | The Platform screen's second half: connected, the average per gateway, resumed (refused handshakes in the line under the running head while there are none); the distribution of clients across gateways; the gateways more than a tenth from the average (at most three rows) | A row per gateway; per-client lists |
| Changes | What changed before it broke? | One master–detail screen: the last hour's changes, newest first, as the list (time, kind, target; the bad one in the accent); the selected change's detail beside it (what, who, the result, the finding), a bad rollout's roll back, and a link to its target in the cluster. Changes come from what membership shows (a build changing across a type, a join, a leave, a new incarnation), the Dashboard's own actions, and what the deploy pipeline reports; with no Kubernetes API the Dashboard does not name an autoscaler, it says "Seen in membership". A change older than the hour is one link away, in Grafana's annotations | Deploy logs; the full rollout timeline |
| Rollout and rollback | Roll back the bad build | The primary action on the incident and on the rollout's detail, confirmed by typing the server type's name and a reason. A new build, the previous one included, reaches the nodes through the deploy (ADR-093): the Dashboard calls the deploy pipeline's webhook with the server type, the build it expects to leave and the build to return to, and the pipeline refuses if the type has moved on; it runs one concurrency group per type (GitHub Actions: `cancel-in-progress: false`), one node at a time, and a run it refuses is the action's Failed. That group keeps one run going and one pending, and a newer pending run cancels the older pending one whatever the setting, so a release from CI can cancel a pending Dashboard rollback: a cancelled run is the action's Failed, and the page says so. Rollouts that CI starts are kept apart by the pipeline's group, not by the Dashboard's lock, which only knows the Dashboard's own actions. While it runs, the action reads "Rolling back, 12 of 400 on 20-2" from the builds in membership and the type's other actions wait. A rollback during a half-finished rollout names the build that is expected to leave, the minority one included ("12 nodes on 20-3 return to 20-2"). Without a webhook it shows the command to run | A separate deploy page |
| Restart, hot restart | Restart a node; restart a type on its build | Restart on the node's row, one of the type's actions like any other (it takes the type's lock, so it waits while a rollback or hot restart runs): the Dashboard calls the deploy pipeline's webhook (`DaServer:Dashboard:DeployWebhook`) for that one node with its incarnation as seen, and the rollout loop hands the node over to a standby of the same build (ADR-098), or retires a gateway or stateless node for the platform to replace; the loop refuses an incarnation the node no longer is. The confirmation says when another node of the type is not running, and the type restarts one node at a time. A node that took over after a crash in the parked, idle-sized container reads "Requests swapped since takeover" (the node's `daserver_node_requests_low`, alert `DaServerNodeRequestsLow`) The node asks for that resize-only run itself (ADR-094), so the state normally clears within a minute; while it lasts the row offers Resize, the same webhook with `--resize-only` under the type's lock, for when the node's own request keeps failing. It swaps the two containers' requests back in place and restarts nothing. Hot restart under the node table (a same-image roll of the type: the Dashboard calls the deploy pipeline's webhook, which runs the one rollout loop, one node at a time and the coordinator last, with scale-in paused; without a webhook it shows the command, `aspire do {cluster}-restart`), confirmed by the type's name and a reason, and disabled while the type has another action running. The Dashboard never loops over nodes itself: a restart of the Dashboard would leave a half-rolled group, and only the loop pauses scale-in. Taking a node out for good is the platform's scale-down | Bulk selection |
| Config tables | Push a new table | An action in Find, confirmed by typing the table's name and a reason. Two phases: every node validates and stages the table, then all switch together; if any node has not staged it within a minute, nothing switches and the push is aborted (the audit says which nodes did not answer). A node that joins meanwhile reads the table from the store. A push takes the cluster's lock (the sentinel type `*cluster`), so two pushes never run at once; it goes through the nodes' admin plane, not the pipeline, so with the store down it is refused with no command to run instead | An editor |
| An actor | What state is this actor in? | One master–detail screen: the six slowest actors in the cluster as the list (with any found by its id through Find), the selected actor beside it in full: handler p99, owning node, requests, mailbox, type, last saved, its components and its last log lines (from Loki), with a link to its traces (Grafana Explore over Tempo) | Lists of all actors; raw JSON dumps; live editing |
| Audit | Who did what? | One row per action: time, who, action, target and reason, and its latest outcome with the action id, newest first, paged. The sink holds two rows or more per action (its intent, then each outcome); the screen merges them by action id and shows the latest. Read from the audit's durable sink (see Actions) | Filters and search; export (the audit is a table in the store, or a file) |
| History | What happened last week? | A link on each view to Grafana at the same time range (the shipped dashboard, or Explore for logs and traces) | Our own long-term storage, ad-hoc queries, custom dashboards |

## Actions

Every action runs on the Dashboard's server, never in the browser, so closing the page does not stop or repeat it.

- **What the operator saw.** An action carries it: a node's incarnation, a type's build. The target refuses an action
  whose precondition has moved on, and the confirmation checks again when it is submitted.
- **Once.** Each action has an idempotency key: the action, the target and what was seen, never the operator, so a
  second press, a retried call or another operator's identical request within ten minutes of the first request is the
  same action, and the page says so ("Already requested as action 7f31"). An action that has finished (Done or Failed) no
  longer dedupes: a second hot restart of the same build an hour later is a new action. The submit button
  disables on its first press and enables again when the server refuses, so a "wait" can be retried without retyping.
- **One at a time per server type.** Every action on a type takes the type's lock: a rollback, a hot restart, a
  rollout and a node's restart alike. While one runs, the type's other actions wait, and their buttons say which action
  is running. The lock is the store's `daserver_actions` table: a unique partial index on `(type)` over the rows whose
  state is not terminal (Requested, Accepted, Running, Unknown or Stalled) admits one live action per type; an action on
  the whole cluster (a config push) locks the sentinel type `*cluster`, since a unique index never conflicts on NULL. The
  row carries a lease (`claimed_by`, `lease_until`, three cadences long, on the database's clock) that the replica
  running it renews every cadence. A replica that finds an expired lease (its owner crashed) claims the row with one
  compare-and-set (`UPDATE ... SET claimed_by = $me, lease_until = now() + $lease WHERE action_id = $1 AND lease_until <
  now()`), so of two replicas that see it at once one wins, and only then reconciles: it asks the deploy pipeline for the
  run carrying the action id and sets the row's state from it. A dispatch creates its run asynchronously, so "no run
  yet" waits a grace period (two minutes) before the action is Failed, never sends it again; a lookup that fails (the
  pipeline's API down or rate-limited) leaves the state as it is and the lock held. A lease that ran out during a store
  outage is claimed first by the replica that was following the run, which still holds it in memory. Without a store
  there is no shared lock, so the Dashboard runs as one replica.
- **Outcomes until the end.** An action's state is Requested (written, before the call), Accepted (the pipeline took
  it), Running, then Done or Failed, the only terminal states; Unknown (the call timed out, so it may have run) and
  Stalled hold the lock like a running action. After Accepted, the Dashboard follows the run: by the pipeline's
  completion callback when it has one, or by polling the run every cadence. The callback is signed (HMAC over the body,
  which carries the action id, with the key in the `{cluster}-deploy` Secret that also holds the webhook's and the run
  lookup's tokens), since it can release a lock and carries no sign-in. It is Stalled when membership shows no progress
  (no node moved build or incarnation) for ten minutes while the run still says running; Stalled is shown in the accent
  with the run's link and releases nothing until an operator marks it Failed. Marking it Failed is an action of its
  own: act scope, a reason, audited, and refused while the store is down (the page says the lock stays until it
  answers); it releases the lock and does not stop the run, which the pipeline cancels. The lock
  is held until a terminal state, however long a roll of 400 nodes takes, and a same-image hot restart, which changes
  no build, still holds it. A call that timed out is Unknown until the run lookup finds it; a webhook that answers
  without a run id (GitHub's `workflow_dispatch`) is not retried after a timeout: the pipeline is asked for runs
  carrying the action id first.
- **Reasons.** An action on a whole server type or the cluster (roll back, hot restart a type, push a config table)
  asks why, and the reason is written to the audit with the operator's name.
- **Audit.** Every action is written at least twice: its intent before the call and each outcome after, with the same
  action id, which the page shows. The audit's sink is durable: the store's `daserver_audit` table when the cluster has
  a store (insert-only for the Dashboard's role), otherwise an append-only file; how long it keeps rows is the
  operator's setting. The same lines also go to the logs, for Loki.
- **No store, no action.** The intent, the idempotency key and the type's lock all live in the store, so when it
  cannot be reached the Dashboard acts on nothing: the confirmation says "The store cannot be reached: nothing was
  sent", and gives the deploy pipeline's command for the same action, whose own run log is the audit for it. The rollout step
  takes one shape for every action: `aspire do {cluster}-restart --type <type>` for a hot restart, plus `--build <build>`
  for a rollback, `--node <node> --incarnation <n>` for one node, and `--resize-only` for a Resize. A config push and
  marking an action Failed have no pipeline command: they need the store, and wait for it. An outcome that cannot be written after the call is kept in a local spill
  file and written when the store answers again, in order, before any new action.
- **Silences.** "Silence for an hour" needs the act scope, is audited like any action, and creates an Alertmanager
  silence with the incident's matchers (`alertname`, `cluster`, `type`) that expires; the incident stays listed, last,
  with the time it ends. The Dashboard writes silences with an Alertmanager credential of its own.

### Who may act

- **An admin key of its own.** The Dashboard holds a separate admin key (its own Secret), never the cluster's signing
  secret: a compromised Dashboard cannot pose as a node or sign a client's resume ticket. Nodes accept admin requests
  signed with it.
- **Two scopes.** View (readings, membership, an actor's state) and act (restart, rollback, hot restart, silence,
  config push). A view token cannot act. Component values are shown to the view scope only where the deployment allows
  it (they can hold personal data).
- **Sign-in.** OIDC with a cookie: the Data Protection key ring is shared by every replica (the store, or a mounted
  key), so a restart or a second replica keeps sessions. The cookie is `SameSite=Lax`, so a link pasted from the pager
  or chat and the identity provider's redirect back both arrive signed in; every action carries an antiforgery token,
  which is what stops a forged request. An expired session answers the page's calls with 401, never a redirect: the
  confirmation keeps what was typed (the name and the reason) and says "Sign in again in a new tab, then press
  Restart", so nothing is retyped.
- **No markup from data.** Log lines, actor keys and component values are data: the page writes them as text, never
  as markup, and is served with a strict Content Security Policy (`default-src 'self'`, scripts from its own origin
  only, no inline script, `frame-ancestors 'none'`). The fonts and the chart library are served by the Dashboard
  itself, never from a CDN; bar heights and motion are set through the style object (CSSOM), which the policy allows,
  never as `style` attributes in markup. The prototype, a single file, loads its fonts and uPlot from CDNs and writes
  style attributes, so it cannot run under this policy itself. Links to Grafana are plain navigations in a new tab
  (`rel="noopener"`), which the policy does not restrict.

## What we deliberately do not do

- No ad-hoc queries and no dashboard editor. Long-term history stays in Prometheus, Loki and Tempo, read through
  Grafana, and every view links there.
- No thresholds of our own: alerts are Prometheus's rules.
- No status colours (red, amber, green), no badges, no icons and no avatars.
- No widgets that look busy without answering a question: no gauges, donut charts, flat sparklines or counters of
  healthy things.
- No modal dialogs. Find and confirmations open as pages, and Esc goes back.
- No second approver. An action is one operator's, with a reason, and the audit says whose.
- No scrolling pages and no sideways scrolling, on phones included.
