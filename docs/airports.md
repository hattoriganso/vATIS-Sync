# Airports

Everything about the airport list on the left of the window: where the airports come from, what the words under them
mean, what each airport's page shows, and where an airport's own settings are kept.

- [Where the airports come from](#where-the-airports-come-from)
- [The airport list](#the-airport-list)
- [An airport's page](#an-airports-page)
- [Settings for one airport](#settings-for-one-airport)
- [The Airports folder](#the-airports-folder)

## Where the airports come from

The list is filled only from the vATIS profile you have open. There is no way to type an airport in: an airport that is
not in your profile cannot be synced, so listing it would do nothing.

- **When it is read.** When the app starts, and when you press **Refresh** in the left column. It is not checked again by
  itself, so a station you add in vATIS appears after the next Refresh.
- **Which stations count.** A station is used only if it has a preset named exactly `D-ATIS`. Capital letters and the
  hyphen matter: `d-atis`, `DATIS` and `D ATIS` do not match. The app writes the real-world text into that preset.
  A brand-new, empty station has no such preset, so it is skipped. The Log names every skipped station
  (`Skipped 1 station in your vATIS profile with no preset named "D-ATIS": KJFK.`).
- **What an airport is.** The first four letters of the station name. `KMIA_A` (arrival) and `KMIA_D` (departure) are two
  stations of one airport, `KMIA`.
- **If vATIS cannot be read.** A yellow bar says `Couldn't sync with vATIS, retrying in N seconds`, and the countdown
  runs down by itself. When it ends the app tries again, and starts a new 15-second countdown if that fails, until vATIS
  answers. **Retry now** tries immediately and restarts the countdown if that fails too. Every failed attempt and every
  success is written to the Log.
- **Nothing is remembered.** The list starts empty each time the app starts and fills as vATIS answers. Each airport's
  own settings are kept on disk anyway (see [The Airports folder](#the-airports-folder)).

## The airport list

The **Overview** tile at the top shows the overall status, your stations and the Log. The airports are listed under
**AIRPORTS**, with a small status word under each:

| Word | Meaning |
|---|---|
| **Ready** | In the profile, but nothing has been written into vATIS yet (the sync is not running, or has not reached it). |
| **Synced** | The real-world D-ATIS text has been written into its vATIS station, but the ATIS is not connected. A station with no real-world data to write does not count. |
| **Started** | At least one of its stations is connected, so the ATIS is live. |
| **Desynced** | You disregarded a change, so vATIS differs from the real-world ATIS. Shown in amber in place of Started or Synced until the two agree again (see [Approving changes](approval.md#when-vatis-no-longer-matches)). The airport's page has a **Resync** button for it. |
| **No D-ATIS** | One of its stations is connected, but there is no current real-world D-ATIS to put in it. Shown in amber in place of Started. |
| **Stale** | It was in the list, but a later read of the profile did not include it. It stays listed, in amber, for the rest of the session so you notice. Its settings are kept and nothing is synced for it. The minus button hides it from the list; the next time vATIS reports it, it comes back. |

The dot beside each airport is filled when one of its stations is connected and a ring when none is. It is green,
and turns amber for **Desynced** and **No D-ATIS**: the ATIS is live, but it is not what the real world says.

An empty profile (no station with a D-ATIS preset) marks every listed airport stale and shows a warning.

## An airport's page

Under the airport's name are two tabs. **Summary** is shown first; each airport remembers which tab you last used for the
rest of the session.

### Summary

One card for each station of the airport. A card shows:

- where the station stands (ready, synced or started),
- which information letter this is and how long ago it was issued, for example `Information P, issued 12 minutes ago.`,
- the **Airport conditions** and **NOTAMs** text, as it would be written into vATIS.

The checkbox **Show what was changed from the real-world text** compares that text with the raw real-world D-ATIS:
words that were taken out are struck through in red, and words that were put in (a replacement frequency, for example)
are highlighted in green.

It works before you press Start. The app downloads the real-world D-ATIS from atis.info (the same source the sync uses)
and applies this airport's rules and frequency settings itself. It writes nothing into vATIS.

- It follows the settings: change a rule or frequency and the text updates a moment later.
- A download is reused for a minute, so moving between airports does not download again each time. **Update now** forces
  a fresh download.
- If an airport has no real-world D-ATIS, or it is more than an hour and three quarters old, the card says so instead of
  showing text. If the download fails but an older copy exists, the older copy is shown with a yellow note.
- It leaves out two things that live in vATIS: your vATIS contractions, and text you deleted by hand (see
  [Deletion rules](rules.md#deletion-rules)). The real text written into vATIS may differ from the preview in those two ways.

### Settings

The rules and frequency settings for this airport. See the next section.

## Settings for one airport

Two kinds of settings can differ from airport to airport: which [built-in rules](rules.md#built-in-rules) are on, and how
each position's [frequencies](rules.md#frequencies) are handled.

- **Defaults.** The gear icon opens the Settings window. At the bottom, **Default rules for all airports** and **Default
  frequencies for all airports** apply to every airport that has not chosen otherwise.
- **Customizing.** On an airport's Settings tab, each of the two sections has **Use the default settings**. Untick it to
  give that airport its own copy, which starts from the defaults as they are at that moment. The two sections are
  independent: an airport can use its own frequencies and the default rules.
- **Following the defaults again.** Tick the box. If both sections follow the defaults, the airport has no settings file.
- Settings are saved as soon as you change them. While the sync is running, changes apply the next time you press Start.

## The Airports folder

Each airport's own settings are saved in a file of their own:

```
%APPDATA%\VatisSync\Airports\KSFO.toml
```

- The file is written the moment you change that airport's settings, and read when the airport appears in your profile.
- It is kept whether or not the airport is in the profile that is open, so settings survive between sessions even while
  the airport is not loaded. The sync uses every file in the folder, listed or not.
- An airport that follows the defaults in both sections has no file; one that goes back to the defaults loses its file.
- The format is [TOML](https://toml.io), so the file can have comments. The app puts short explanations at the top of
  every file it writes:

```toml
# vATIS-Sync settings for KSFO only.
# The app writes this file whenever you change this airport's settings. You can edit it while the app is
# closed. Comments you add are not kept the next time the app saves it.

# true: this airport follows the default rules chosen in Settings. false: it uses the list below.
use-default-rules = false
# Built-in rules switched OFF for this airport (only used when use-default-rules is false).
# Rule names: flight-service-pointers, drone-laser-reports, tfr-notam-pointers
disabled-rules = ["drone-laser-reports"]

# true: this airport follows the default frequency settings chosen in Settings. false: it uses the table below.
use-default-frequencies = false
# One line per position. mode is "Remove" or "Override"; frequency is only used with "Override".
# Positions left out keep the real-world text. Positions: ClearanceDelivery, Ground, Tower, Departure, Approach, Ramp, Metering, Helicopter, Other
[frequencies]
Ground = { mode = "Override", frequency = "121.9" }
Tower = { mode = "Remove" }
```

Editing by hand:

- Do it while the app is closed. The app rewrites the file whenever you change that airport, and a comment you add is
  not kept when it does.
- Keys that are missing mean "follow the defaults". Keys the app does not know are ignored, and so are positions or modes
  it does not recognize.
- A frequency must be written like `121.900`, between 118.000 and 136.999. An invalid one is treated as if the position
  were set to keep the real text.
- If a file cannot be read (it is not valid TOML), the app sets it aside as `KSFO.toml.broken`, warns you, and uses the
  default settings for that airport. What you wrote is not overwritten.

### From older versions

Earlier versions kept the airport list and each airport's settings inside `settings.json`. The first time the new version
starts, the per-airport settings are moved into the Airports folder (a file that already exists is left alone) and removed
from `settings.json`. The airport list itself is no longer remembered.
