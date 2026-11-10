# Log files

The **Log** on the Overview shows what has happened in this run, and it is cleared when you press **Clear** or close the
app. The log files keep the record, so that if something goes wrong you can look at what happened afterwards, and
anyone helping you can read it.

- [Where the files are](#where-the-files-are)
- [What is in them](#what-is-in-them)
- [The detailed record](#the-detailed-record)
- [Errors](#errors)
- [Sending a log to someone](#sending-a-log-to-someone)

## Where the files are

In `%APPDATA%\VatisSync\logs`. Press **Open log folder**, above the Log on the Overview, to open it.

- **One file per day**, named `activity-2026-11-10.log`, written as the day goes on. Open it with any text editor, even
  while the app is running.
- **Kept for 14 days.** Older files are deleted automatically.
- **Capped at 10 MB a day.** If a day's file gets that large it is moved aside as `activity-....log.old`, replacing an
  older `.old` for that day, and a fresh one is started.
- `errors.log` is separate: see [Errors](#errors).

## What is in them

One entry per line, starting with the date and time to the millisecond and a level:

```
2026-11-10 14:03:22.123  INFO   Read your vATIS profile: 3 D-ATIS airports found.
2026-11-10 14:08:22.456  OK     KPHX: update approved (The runway configuration changed: ...)
2026-11-10 14:13:22.789  WARN   KPHX: change not applied, vATIS now differs from the real-world ATIS (...)
2026-11-10 14:13:23.001  ERROR  Could not reach vATIS on port 5000.
2026-11-10 14:13:23.050  TRACE  KPHX: held, nothing written
```

| Level | What it is |
|---|---|
| **INFO**, **OK**, **WARN**, **ERROR** | Exactly the lines of the Log on the Overview, and the messages that appear in the bar at the top. |
| **TRACE** | The detailed record, below. |

Each run starts with a line giving the app version, the Windows and .NET versions, and where the settings and the logs
are, and ends with `vATIS-Sync exiting`.

## The detailed record

On by default, under **Settings**, in **Log files**: *Keep a detailed record of what vATIS says*. It adds **TRACE**
lines with:

- **Every message sent to vATIS and every message it sends back**, as it was sent (`-> vATIS ...` and `<- vATIS ...`),
  including the text written into the ATIS. This is how to find out what vATIS really answers.
- **Messages that were skipped**, replies that did not arrive in time, and why a connection failed.
- **Each refresh**: whether the real-world D-ATIS was fetched, and for each station whether it is connected, whether
  there is real-world text for it, and whether vATIS's text differs.
- **Every decision about approval**: no runway change so it was written without asking, asked the controller, what the
  controller chose, held because it was disregarded, or written.
- **Status changes** of the sync.

Turn it off if you would rather keep only the plain Log. Nothing else changes; the plain entries are always written.

## Errors

An error nothing else handled is written in full, with its stack trace, to `errors.log` in the same folder, and a short
line to the activity log pointing at it. The app keeps running and the bar tells you where the details are.

## Sending a log to someone

The files contain the airports you sync, their station names and the ATIS text. Send the file for the day it
happened, `activity-<date>.log`, and `errors.log` if there is one, and say roughly what time you noticed the problem.
