# Approving changes

Once you are connected to an ATIS, a change to its text reaches the pilots at once. A runway change is the one that
matters most: a pilot told "runway 26" a minute ago, and then "runway 8", has been caught out. So before a refresh
changes a connected ATIS, the app can show you exactly what would change and wait for your decision.

- [When you are asked](#when-you-are-asked)
- [The window](#the-window)
- [The three choices](#the-three-choices)
- [One airport, one answer](#one-airport-one-answer)
- [When vATIS no longer matches](#when-vatis-no-longer-matches)
- [What counts as a runway change](#what-counts-as-a-runway-change)
- [What never asks](#what-never-asks)

## When you are asked

Set it under **Settings**, in **Changes to a connected ATIS**:

| Setting | You are asked when |
|---|---|
| **Ask me when the runways change** (the default) | The runways or the approach types in the text change. |
| **Ask me before any change** | The Airport Conditions text would change in any way, even a closed taxiway. |
| **Apply changes automatically** | Never. Every refresh is written without asking. |

While the question is open the sync waits, and the Overview says `Waiting for your approval (KPHX)`. A tray
notification also tells you a question is waiting. Other airports are not updated until you answer.

An airport is asked about as a whole. KMIA_A (arrival) and KMIA_D (departure) are two stations of one airport, and
they are always synced together or not at all, so the answer covers every station of the airport. See
[One airport, one answer](#one-airport-one-answer).

## The window

The window is titled `Approve ATIS change - KPHX` and stays on top of other windows. It does not block the app.

- **Headline.** `KPHX: runways changed` when the runways or approaches differ, with a line under it that says how
  (`From runways 26; ILS 26 to runways 8; ILS 8.`). Otherwise `KPHX: the ATIS text changed`, with no second line.
- **Two panes.** **Now in vATIS** on the left, and **New (from the real-world D-ATIS)** on the right. The words that
  differ are highlighted, red on the left and green on the right, and a run of changed words is highlighted as one
  phrase.
- **More than one station.** When several stations of the airport would change, each gets its own pair of
  panes, one under the other, headed by its name and what changed (`KPHX_A: from runways 26; ILS 26 to runways 8;
  ILS 8`). The window scrolls if they do not all fit.

## The three choices

| Button | What it does |
|---|---|
| **Apply** | Writes the new text into vATIS, for every station of the airport. |
| **Disregard** | Leaves vATIS as it is, for every station of the airport. You are asked again only if the real-world ATIS changes again. |
| **Disregard KPHX for this session** | Leaves vATIS as it is, and stops asking about that airport until you stop and restart the sync. |

Closing the window with the X, or pressing Esc, counts as **Disregard**. Stopping the sync while a question is open
closes it too, with the ATIS left as it is.

Nothing is ever applied except by pressing **Apply**. There is deliberately no "apply and stop asking": a runway
change must never reach a connected ATIS without you having seen it.

The session button works for one airport only. Other airports still ask. An airport you disregarded for the session is
left alone for any change that would have asked you, so use it for an airport you are happy to leave as it is, and
restart the sync when you want it to follow the real world again. Changes that would not have asked still go through
as usual (a taxiway change in the default setting, say).

## One airport, one answer

The stations of an airport are never out of step with each other: either all of them are in step with the real-world
ATIS, or none are. Two stations of one airport that told pilots different runways would be worse than either one alone.
So:

- **One question per airport**, however many of its stations changed. It is asked when a connected station would
  change in a way that needs approval (the runways, or anything, by your setting), and it lists every station of the
  airport that would change, even one whose own change is harmless, because all of them are applied or held together.
- **Apply writes every station** of the airport, connected or not.
- **Disregard holds every station**, connected or not. That includes a station whose own change was harmless: if the
  other needs your approval, the airport waits as a whole.
- **Disregard for the session** covers the airport, so neither station asks again.

## When vATIS no longer matches

Disregarding a change means vATIS now differs from the real-world ATIS, and you should never be unaware of that.

- **Desynced.** The airport's status word in the list (it is shown for the airport, never for one station) turns amber and reads **Desynced**, in place of Started or
  Synced, for as long as a change you disregarded is still waiting. It goes away by itself when vATIS and the
  real-world ATIS agree again, or when you press **Apply** the next time you are asked.
- **The Log.** For an airport disregarded for the session, each new change that would have asked is written to the Log
  once, as `KPHX: change not applied, vATIS now differs from the real-world ATIS (...)`, in amber. The same text does
  not repeat on every refresh.

## What counts as a runway change

The app reads the Airport Conditions text and picks out the runways in use and the approach types expected (ILS, RNAV,
visual and so on). Two texts are the same flow when both lists match. Only sentences that describe the flow count:
runways in use, landing, departing, and expected approaches. A sentence about a closed runway, hold-short
instructions or a crane does not, so mentioning a closed runway is not taken as a flow change.

## What never asks

- **The first fill after you press Start.** You asked for it by pressing Start.
- **An ATIS that is not connected.** Nothing is live, so there is nothing to protect. It is updated freely.
- **A station with no text in vATIS yet.** There is nothing to compare against. (Its airport's other stations may still ask.)
- **A refresh that changes nothing.**
- **A change you already disregarded.** The same real-world text does not ask again. New text does.
