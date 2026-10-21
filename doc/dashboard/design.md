# Dashboard design

How the DaServer Dashboard looks, moves and is laid out. [prototype.html](prototype.html) is a working
prototype with example data; open it in a browser and use the keys at the bottom.

## Character

Editorial minimalism, quiet luxury. A page of a well-set book, not a control room. It is calm, exact and focused. One
screen does one thing, positions never move, and only the text that changed moves.

The Dashboard is an operator's tool. Every screen answers one question an operator asks, and the next step is always
obvious.

## Type

| Role | Face | Size |
|---|---|---|
| Display (page title, headline figure, actions) | Shippori Mincho, falling back to Noto Serif JP, Hiragino Mincho, Songti SC, Georgia | Title `clamp(40px, 4.3vw, 62px)`, headline figure `clamp(92px, 10.5vw, 150px)`, figures 28–38 px, actions 19–23 px |
| Text (body, tables, labels) | Newsreader, falling back to Iowan Old Style, Palatino, Georgia | Body 17 px (16 px on phones), lead 17–20 px, labels 14 px |
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

## Structure

- Hairlines only. There are no cards, boxes, panels, badges, pills, round icons or shadows, and every corner is
  square.
- Generous whitespace. Content is one text column beside a narrow margin column. The margin holds the folio
  ("2 of 5"), what comes next, and the latest status line.
- One table per entity. Rows have the same height, and columns never mix kinds.
- Every number sits beside its normal: a figure carries "usually 12 ms" or "of 4 nodes" beneath it, and a reading is
  never shown alone.
- One selected-state indicator. The current tab, the current list row and the current find result are shown by ink
  weight and darkness only, with no underline bars, backgrounds or markers.
- Actions are words with a rule beneath them. The primary action is in the accent and the secondary in the second
  ink. A link to history (Prometheus, Loki, Tempo) is the smallest text action.

## Focus

- One screen, one thing. A view that does not fit becomes two screens in the same section. Nothing is shrunk to fit
  and the page never scrolls.
- Fixed positions. Header, tabs, title, margin and key hints sit in the same place on every screen. Each layout
  reserves the space of its tallest state: tables keep empty rows, and the "Next" line keeps its height when there is
  nothing next.
- Nothing appears twice. Each fact has one home, and other screens link to it.
- What is wrong is in the accent. When nothing is wrong, nothing is in the accent.

## Navigation

| Input | Does |
|---|---|
| ↓ / ↑, wheel, vertical swipe, PageDown / PageUp, j / k | The next or previous screen within the current section. It stops at the section's first and last screens and never crosses into another section |
| → / ←, horizontal swipe, a tab | The next or previous section, opening at its first screen |
| A row in a list | Opens that item; Esc returns to where you were |
| `/` or ⌘K / Ctrl+K | Find: actors, nodes and actions, filtered as you type; ↑↓ select, Enter opens |
| Esc or Backspace | Back. It closes Find or a confirmation, otherwise returns from the last jump (a list row, a Find result). It is dimmed while there is nowhere to go back to |

- One gesture is one step. A trackpad flick, with its inertia tail, moves one screen; a new flick moves again
  (Lethargy tells them apart).
- Every key hint at the bottom is bold, always in the same place, and clickable. A key that only one screen uses
  (such as rolling back) is a button on that screen, not a global key.
- Every action that changes the cluster asks you to type the target's name, and it is written to the audit log.

## Motion

No fades, no eases on whole blocks, no scrambled text, no wipes.

| Change | Motion | Timing |
|---|---|---|
| Same layout, new values (the next server type, the next list item) | Text morph: unchanged characters stay; a changed run blurs out where it stood (6 px blur), and its replacement blurs in | Out 200 ms, in 320 ms after a 70 ms delay |
| A new page | The page's lines slide in from the direction of travel, 30 px vertically or 44 px horizontally, staggered | 360 ms, 26 ms stagger, `cubic-bezier(.2, .75, .25, 1)` |
| Reduced motion | Text and pages change in place | 0 |

## What each job looks like

DaServer is a general framework, so every view below names server types, actors and nodes rather than any game.

| Job | The operator's question | Presentation | Left out |
|---|---|---|---|
| Triage | Is anything wrong right now? | The Incident section's first screen: what is wrong as the title, the figure that proves it beside its limit and usual value, one sentence of cause, the fix as the primary action. When nothing is wrong, the title says so and the section has one screen | Status grids, counts of healthy things, coloured dots |
| Evidence | Why do we think so? | The next screens of the same section: the last hour as one chart (the culprit in the accent, others in ink, the limit dashed, the change that caused it marked), then the relevant log lines | Dashboards of every metric; history beyond the window (one link to Prometheus or Loki) |
| Cluster health | Which server type is unhealthy? | The Cluster section's first screen: one row per server type (nodes, actors, ask p99, build), the unhealthy row in the accent, a sentence on the store and membership | Per-node detail on the overview |
| A server type | How are its nodes? | Four figures with their usual values, then one row per node (state, CPU, memory, p99) with Drain on each, then Hot restart | Sparklines that are flat; graphs per node |
| Membership | Do all nodes agree on who is in the cluster? | One phrase above the overview title ("9 nodes, every view agrees"); a disagreement becomes the incident | A separate membership page while views agree |
| Store | Are actors being saved? | Write p99, writes in flight, unsaved actors and the oldest unsaved age, errors | Query plans, per-table statistics |
| Client connections | Are clients connected and balanced? | Connected, resumed, spread from the average, refused handshakes; one row per gateway | Per-client lists |
| Changes | What changed before it broke? | A list of the last hour's changes (newest first) beside the selected change's detail; a bad rollout carries its own roll back | Deploy logs; the full rollout timeline |
| Rollout and rollback | Roll back the bad build | The primary action on the incident and on the rollout's detail, confirmed by typing the server type's name | A separate deploy page |
| Drain, hot restart | Take a node out; restart a type | Drain on the node's row; Hot restart under the node table; both confirmed by typing the name | Bulk selection |
| Config tables | Push a new table | An action in Find, confirmed by typing the table's name; accepted only when every node accepts it | An editor |
| An actor | What state is this actor in? | Find it (or pick it from the slowest list): type, node, requests, mailbox, last saved, its components and its last log lines, with a link to its traces in Tempo | Raw JSON dumps; live editing |
| Audit | Who did what? | One row per action: time, who, action, target, newest first, paged | Filters and search; export (the audit log is in the logs too) |
| History | What happened last week? | A link on each view to Prometheus, Loki or Tempo at the same time range | Our own long-term storage, ad-hoc queries, custom dashboards |

## What we deliberately do not do

- No ad-hoc queries and no dashboard editor. Long-term history stays in Prometheus, Loki and Tempo, and every view
  links there.
- No status colours (red, amber, green), no badges, no icons and no avatars.
- No widgets that look busy without answering a question: no gauges, donut charts, flat sparklines or counters of
  healthy things.
- No modal dialogs. Find and confirmations open as pages, and Esc goes back.
- No scrolling pages and no sideways scrolling, on phones included.
