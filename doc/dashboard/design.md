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
  over the page and the environment at the right edge. The footer's key hints start on the page's first column.
- **Placements on the page's 10 columns.** A screen uses one of three compositions:
  - full: 10 columns, for a chart, a table or a row of figures;
  - main and aside: 6 columns, one empty column, then 3 columns;
  - list and detail: 4 columns, one empty column, then 5 columns.
- **Rhythm.** The vertical unit is 8 px. Spacings are 8, 16, 24, 32 and 40 px, and every table row, property row and
  list row is 44 px (42 px on phones). The body starts 40 px below the title, and units inside it are 32 px apart.
- **Lines that line up.** The folio's number sits on the title's baseline, in the title's size. "Next" starts on the
  body's first line. In a list and detail, both start on the same hairline.
- **Centred per section.** Head and body are centred as one block between header and footer. The body row is as tall
  as the tallest screen of the current section, measured, so nothing moves within a section and no section hangs from
  the top over an empty lower half.
- **One focal point.** Each screen has one place the eye lands first (the incident's figure, a type's four figures, a
  chart, the selected list row), then a second (the table or the detail), then the action.
- **Phones.** One column, in the same order. Views that do not fit become more screens. The folio and "Next" move to
  one line above the key hints.

## Components

There is one vocabulary, used the same way everywhere.

- **A unit** is a caption, then a figure, table or chart, then its foot. In the foot, actions come first and the
  history link ("Open in Prometheus") sits at the end, on the unit's right edge. An action sits where the decision is
  made: Drain on a node's row, Hot restart in the foot of the node table it restarts, a rollback in the rollout's
  detail.
- **A figure** is a label above, the reading in the display face, and its normal below ("usually 12 ms",
  "of 4 nodes"). A reading is never shown alone.
- **A table** has one entity per table, one row height and hairlines that run unbroken. Text columns are left
  aligned, and numbers are right aligned in tabular figures. The gutter is padding on the side away from the
  alignment.
- **Actions.**
  - The primary action is in the accent.
  - The secondary action is in the second ink.
  - Both are words in the display face with a rule beneath them.
  - A row action (Drain) and a history link are the small text action.
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

- One screen, one thing. A view that does not fit becomes two screens in the same section. Nothing is shrunk to fit
  and the page never scrolls.
- Fixed positions. Header, tabs, margin and key hints never move. The title stays put within a section, and the
  "Next" line keeps its height when there is nothing next.
- Nothing appears twice. Each fact has one home, and other screens link to it.
- Cut what does not answer the screen's question. If a sentence restates the table under it, or the eyebrow repeats
  the title, it goes.

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
   - A new character blurs in: it starts at 4 px blur and opacity 0, slightly low, and settles.
   - A removed character blurs out where it stood: it blurs, rises slightly and fades.

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
| Characters blurring in | 336 ms, after an 84 ms delay | the same |
| Characters blurring out | 252 ms | the same |
| New page, per block | 360 ms, 18 px vertical or 28 px horizontal, first block at 60 ms, then 35 ms apart | the same |
| Leaving page | 160 ms, half the distance, the opposite way | `cubic-bezier(.4, 0, 1, 1)` |
| Reduced motion | Text and pages change in place | none |

The morph is our own, about a hundred lines of code. Libraries doing the same family of effect are
[NumberFlow](https://number-flow.barvian.me/), for numbers, and [Torph](https://github.com/lochie/torph), for text.
Torph matches whole words when the text has spaces, keeps one instance per element and lays characters out as
inline-block. We match by character across re-rendered pages and need the layout untouched.

## What each job looks like

DaServer is a general framework, so every view below names server types, actors and nodes rather than any game.

| Job | The operator's question | Presentation | Left out |
|---|---|---|---|
| Triage | Is anything wrong right now? | The Incident section's first screen: what is wrong as the title; the figure that proves it beside its limit and usual value, one sentence of cause and the fix as the primary action (main); the affected type's nodes with their readings (aside), which tells a bad build from a bad node. When nothing is wrong, the title says so and the section has one screen | Status grids, counts of healthy things, coloured dots |
| Evidence | Why do we think so? | The next screens of the same section: the last hour as one chart (the culprit in the accent, others in ink, the limit dashed, the change that caused it marked), then the relevant log lines, read from Loki | Dashboards of every metric; history beyond the window (one link to Prometheus or Loki) |
| Cluster health | Which server type is unhealthy? | The Cluster section's first screen: one row per server type (nodes, actors, ask p99, build), the unhealthy row in the accent; membership is the eyebrow | Per-node detail on the overview |
| A server type | How are its nodes? | Four figures with their usual values, then one row per node (state, CPU, memory, p99) with Drain on each; Hot restart in the table's foot | Sparklines that are flat; graphs per node |
| Membership | Do all nodes agree on who is in the cluster? | One phrase above the overview title ("9 nodes, every view agrees"); a disagreement becomes the incident | A separate membership page while views agree |
| Store | Are actors being saved? | Write p99, writes in flight, unsaved actors and the oldest unsaved age, errors | Query plans, per-table statistics |
| Client connections | Are clients connected and balanced? | Connected, resumed, spread from the average, refused handshakes; one row per gateway | Per-client lists |
| Changes | What changed before it broke? | A list of the last hour's changes (newest first) beside the selected change's detail; a bad rollout carries its own roll back | Deploy logs; the full rollout timeline |
| Rollout and rollback | Roll back the bad build | The primary action on the incident and on the rollout's detail, confirmed by typing the server type's name | A separate deploy page |
| Drain, hot restart | Take a node out; restart a type | Drain on the node's row; Hot restart under the node table; both confirmed by typing the name | Bulk selection |
| Config tables | Push a new table | An action in Find, confirmed by typing the table's name; accepted only when every node accepts it | An editor |
| An actor | What state is this actor in? | Find it (or pick it from the slowest list): type, node, requests, mailbox, last saved, its components and its last log lines (from Loki), with a link to its traces in Tempo | Raw JSON dumps; live editing |
| Audit | Who did what? | One row per action: time, who, action, target, newest first, paged; the audit is the Dashboard's log stream, read from Loki | Filters and search; export (the audit log is in the logs too) |
| History | What happened last week? | A link on each view to Prometheus, Loki or Tempo at the same time range | Our own long-term storage, ad-hoc queries, custom dashboards |

## What we deliberately do not do

- No ad-hoc queries and no dashboard editor. Long-term history stays in Prometheus, Loki and Tempo, and every view
  links there.
- No status colours (red, amber, green), no badges, no icons and no avatars.
- No widgets that look busy without answering a question: no gauges, donut charts, flat sparklines or counters of
  healthy things.
- No modal dialogs. Find and confirmations open as pages, and Esc goes back.
- No scrolling pages and no sideways scrolling, on phones included.
