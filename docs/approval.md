# Approving changes

Once you are connected to an ATIS, a change to its text reaches the pilots at once. A runway change is the one that
matters most: a pilot told "runway 26" a minute ago, and then "runway 8", has been caught out. So when a refresh would
change the runways or approaches of a connected ATIS, the app lets you know and holds that airport until you have seen
exactly what would change and decided. It asks about nothing else: weather, a closed taxiway or a new NOTAM never
raises a question. And it never takes the screen to ask: you may be in the middle of a transmission, and the change can
wait a minute.

- [When you are asked](#when-you-are-asked)
- [How you are told](#how-you-are-told)
- [The window](#the-window)
- [The three choices](#the-three-choices)
- [One airport, one answer](#one-airport-one-answer)
- [When vATIS no longer matches](#when-vatis-no-longer-matches)
- [Resync](#resync)
- [What counts as a runway change](#what-counts-as-a-runway-change)
- [What never asks](#what-never-asks)

## When you are asked

Set it under **Settings**, in **Changes to a connected ATIS**:

| Setting | You are asked when |
|---|---|
| **Ask me when the runways change** (the default) | The runways or the approach types in the text change. |
| **Apply runway changes automatically** | Never. Every refresh is written without asking. |

An older version also had "Ask me before any change". It was removed; a settings file that still says so is read as
the default.

An airport is asked about as a whole. KMIA_A (arrival) and KMIA_D (departure) are two stations of one airport, and
they are always synced together or not at all, so the answer covers every station of the airport. See
[One airport, one answer](#one-airport-one-answer).

## How you are told

A change waiting for you never opens a window by itself.

- **A small strip** appears in a corner of the screen (the bottom right unless you choose another one under
  **Settings**, in **Changes to a connected ATIS**): `KPHX: Runways 26 → 8.`, with **Review** and
  **Later**. It stays on top but is never the active window, so nothing you type or press can answer it, and it has no
  Apply button: it only offers to open the review window.
- **Several airports.** When more than one airport has a change waiting, the strip reads `2 airports waiting review`,
  and **Review** opens the one that has waited longest.
- **Later** hides the strip for 3 minutes. If a change is still waiting then, it comes back. An airport that starts
  waiting in the meantime is counted when it comes back.
- **A sound**, if you want one. Under **Settings**, in **Changes to a connected ATIS**, tick *Play a soft sound when a
  change is waiting for my review*. It is off by default. It plays when the strip appears, and again if the strip comes
  back after Later, but not for each change while the strip is already showing.
- **A tray notification** says the same, and **Review waiting changes** in the tray menu opens the review window, for
  anyone who works without the mouse.
- **In the app**, the airport reads **Needs review** in amber (its dot turns amber too, and so does the D-ATIS column
  of its stations on the Overview), and its page has a banner with a **Review** button.

**The sync does not wait for you.** Only the airport with the change waits: nothing is written for it until you decide,
and the other airports keep refreshing on schedule. When you answer, the answer takes effect at once, not at the next
refresh.

- If the real-world ATIS moves again before you have answered, the question is replaced by one about the new text, and a
  review window that was open for the old text closes, so you never apply text you did not see.
- If the real-world ATIS agrees with vATIS again, the question goes away by itself.
- Stopping the sync withdraws every question, with the ATIS left as it is.

## The window

Click **Review** and the window opens in front of the other windows. It is titled `Approve ATIS change - KPHX`. It does
not block the app.

- **Headline.** `KPHX: runways changed`, with a line under it that says how
  (`Runways 26 → 8.`, with the approaches too when they change: `Approaches ILS → RNAV, VISUAL.`).
- **Two panes.** **Now in vATIS** on the left, and **New (from the real-world D-ATIS)** on the right. The words that
  differ are highlighted, red on the left and green on the right, and a run of changed words is highlighted as one
  phrase.
- **More than one station.** When several stations of the airport would change, each gets its own pair of
  panes, one under the other, headed by its name and what changed (`KPHX_A: Runways 26 → 8.`,
  or `KPHX_D: The ATIS text changed.` for a station whose runways stay the same). The window scrolls if they
  do not all fit.
- **Enter does nothing.** No button is the default and none has the keyboard when the window opens, so a stray key
  cannot answer it. Use the mouse, or Tab to a button. Esc is **Disregard**.
- **Apply ignores the first moment.** A click on **Apply** in the first 0.6 seconds after the window opens is ignored,
  so a double-click on **Review** cannot land on it.

## The three choices

| Button | What it does |
|---|---|
| **Apply** | Writes the new text into vATIS, for every station of the airport. |
| **Disregard** | Leaves vATIS as it is, for every station of the airport. You are asked again only if the real-world ATIS changes again. |
| **Disregard KPHX for this session** | Leaves vATIS as it is, and stops asking about that airport until you stop and restart the sync. |

Closing the window with the X, or pressing Esc, counts as **Disregard**. Stopping the sync while a question is open
closes it too, with the ATIS left as it is. Until you answer, the airport stays at **Needs review**.

Nothing is ever applied except by pressing **Apply**. There is deliberately no "apply and stop asking": a runway
change must never reach a connected ATIS without you having seen it.

The session button works for one airport only. Other airports still ask. An airport you disregarded for the session is
left alone for any change that would have asked you, so use it for an airport you are happy to leave as it is, and
restart the sync when you want it to follow the real world again. Changes that would not have asked still go through
as usual (a taxiway change, say).

## One airport, one answer

The stations of an airport are never out of step with each other: either all of them are in step with the real-world
ATIS, or none are. Two stations of one airport that told pilots different runways would be worse than either one alone.
So:

- **One question per airport**, however many of its stations changed. It is asked when a connected station's
  runways or approaches would change, and it lists every station of the
  airport that would change, even one whose own change is harmless, because all of them are applied or held together.
- **Apply writes every station** of the airport, connected or not.
- **Disregard holds every station**, connected or not. That includes a station whose own change was harmless: if the
  other needs your approval, the airport waits as a whole.
- **Disregard for the session** covers the airport, so neither station asks again.

## When vATIS no longer matches

Disregarding a change means vATIS now differs from the real-world ATIS, and you should never be unaware of that.

- **Desynced.** The airport's status word in the list (it is shown for the airport, never for one station) turns amber and reads **Desynced**, in place of Connected or
  Synced, for as long as a change you disregarded is still waiting. The dot beside it turns amber too, and on the
  Overview the D-ATIS column of each of its stations reads **Desynced**. It goes away by itself when vATIS and the
  real-world ATIS agree again, when you press **Apply** the next time you are asked, or when you stop the sync.
- **The Log.** For an airport disregarded for the session, each new change that would have asked is written to the Log
  once, as `KPHX: change not applied, vATIS now differs from the real-world ATIS. Runways 26 → 8.`, in amber. The same text does
  not repeat on every refresh.

## Resync

On the page of a Desynced airport, an amber banner explains why and offers **Resync**.

- **What it does.** It takes back your Disregard for that airport (including *Disregard for this session*) and checks
  the real-world ATIS straight away, without waiting for the next refresh.
- **What you then see.** If a change still needs approval, the strip comes back, **Review** opens the window with what
  would change, and you choose **Apply** or **Disregard** as before. Resync never writes anything by itself: a runway change still
  cannot reach vATIS without you having seen it. If the change no longer needs approval, it is applied like any other.
- **The Log** says `KPHX: resync requested, checking the real-world ATIS now`.

## What counts as a runway change

The app reads the Airport Conditions text and picks out the runways in use and the approach types expected (ILS, RNAV,
visual and so on). Two texts are the same flow when both lists match. Only sentences that describe the flow count:
runways in use, landing, departing, and expected approaches. A sentence about a closed runway, hold-short
instructions or a crane does not, so mentioning a closed runway is not taken as a flow change. When one sentence
does both (`LNDG RWY 9, RWY 27 CLSD`), only the part about the closure is left out, so a change to the runway in use
is still seen.

## What never asks

- **The first fill after you press Start.** You asked for it by pressing Start. Only that one: if vATIS stops
  answering and the app has to start over by itself, an ATIS that is still connected is checked like any refresh,
  and what you disregarded stays disregarded.
- **An ATIS that is not connected.** Nothing is live, so there is nothing to protect. It is updated freely.
- **A station with no text in vATIS yet.** There is nothing to compare against. (Its airport's other stations may still ask.)
- **A change that is not a runway change**, however large: new weather, a closed taxiway, a new NOTAM. Those are
  written as usual.
- **A refresh that changes nothing.**
- **A change you already disregarded.** The same real-world text does not ask again. New text does.
