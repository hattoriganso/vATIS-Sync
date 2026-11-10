# Approving changes

Once you are connected to an ATIS, a change to its text reaches the pilots at once. A runway change is the one that
matters most: a pilot told "runway 26" a minute ago, and then "runway 8", has been caught out. So before a refresh
changes a connected ATIS, the app can show you exactly what would change and wait for your decision.

- [When you are asked](#when-you-are-asked)
- [The window](#the-window)
- [The three choices](#the-three-choices)
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
notification also tells you a question is waiting. Other stations are not updated until you answer.

## The window

The window is titled `Approve ATIS change - KPHX` and stays on top of other windows. It does not block the app.

- **Headline.** `KPHX: runways changed` when the runways or approaches differ, with a line under it that says how
  (`From runways 26; ILS 26 to runways 8; ILS 8.`). Otherwise `KPHX: the ATIS text changed`, with no second line.
- **Two panes.** **Now in vATIS** on the left, and **New (from the real-world D-ATIS)** on the right. The words that
  differ are highlighted, red on the left and green on the right, and a run of changed words is highlighted as one
  phrase.

## The three choices

| Button | What it does |
|---|---|
| **Apply** | Writes the new text into vATIS. |
| **Disregard** | Leaves vATIS as it is. You are asked again only if the real-world ATIS changes again. |
| **Disregard KPHX for this session** | Leaves vATIS as it is, and stops asking about that station until you stop and restart the sync. |

Closing the window with the X, or pressing Esc, counts as **Disregard**. Stopping the sync while a question is open
closes it too, with the ATIS left as it is.

Nothing is ever applied except by pressing **Apply**. There is deliberately no "apply and stop asking": a runway
change must never reach a connected ATIS without you having seen it.

The session button works for one station only. Other stations still ask. A station you disregarded for the session is
left alone for any change that would have asked you, so use it for a station you are happy to leave as it is, and
restart the sync when you want it to follow the real world again. Changes that would not have asked still go through
as usual (a taxiway change in the default setting, say).

## When vATIS no longer matches

Disregarding a change means vATIS now differs from the real-world ATIS, and you should never be unaware of that.

- **Desynced.** The airport's status word in the list turns amber and reads **Desynced**, in place of Started or
  Synced, for as long as a change you disregarded is still waiting. It goes away by itself when vATIS and the
  real-world ATIS agree again, or when you press **Apply** the next time you are asked.
- **The Log.** For a station disregarded for the session, each new change that would have asked is written to the Log
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
- **A station with no text in vATIS yet.** There is nothing to compare against.
- **A refresh that changes nothing.**
- **A change you already disregarded.** The same real-world text does not ask again. New text does.
