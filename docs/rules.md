# Rules

Real D-ATIS contains text that makes no sense on VATSIM, such as "hazardous weather available from Flight Service"
or a drone sighting. Rules remove or rewrite that text before it reaches vATIS.

- [How text is processed](#how-text-is-processed)
- [Built-in rules](#built-in-rules)
- [Airport rules](#airport-rules)
- [Deletion rules](#deletion-rules)
- [Frequencies (planned)](#frequencies-planned)
- [Changing or adding built-in rules](#changing-or-adding-built-in-rules)

## How text is processed

For each station, in this order:

1. The first two sentences of the D-ATIS (the header and the weather report) are dropped. vATIS supplies those itself.
2. The tail `...ADVS YOU HAVE INFO x` is dropped.
3. The NOTAM heading is normalized to `NOTAMS...`. The forms `NOTICE TO AIRMEN`, `NOTICES TO AIRMEN` and
   `NOTICE TO AIR MISSIONS` (with or without a period) all become the same marker.
4. **Built-in rules** run, then **airport rules**, then **deletion rules** (below).
5. Whitespace and punctuation are tidied, and your vATIS contractions are substituted.
6. The text is split at the first `NOTAMS... `. Everything before it goes into *Airport Conditions* and everything
   after it into *NOTAMs*.

Every rule pattern automatically also swallows up to two trailing `,` `.` or `;`, so removing a sentence does not leave
stray punctuation behind. This is also why a rule that matches a *sentence* does not need to include its final period.

## Built-in rules

Built-in rules apply to every airport. They are defined in
[`src/VatisSync.Core/Rules/builtin-rules.json`](../src/VatisSync.Core/Rules/builtin-rules.json) with an id, a name,
a description and one or more patterns. The engine can switch any of them off by id (`SyncOptions.DisabledRules`);
there is no settings screen yet, so end users cannot toggle them until the app is built.

### What counts as a sentence

The patterns use `{S}` as a shorthand for "any text up to a period, but a period between two digits does not end
it". So `118.3` stays in one piece. A pattern written `{S}\bWORD\b{S}` therefore matches the whole sentence
containing `WORD`, from the previous period to the next. Rules that use it remove entire sentences, never part of one.

That is deliberate but has a cost: a matching sentence is removed completely, including anything else useful in it.

### `flight-service-pointers`

Removes sentences that mention any of these whole words or phrases:
`FSS`, `AFSS`, `FLIGHT SERVICE`, `HAZD WX`, `HAZ WX`, `HAZARDOUS WX`.

Flight Service and its hazardous weather briefings do not exist on VATSIM.

Removed in the test capture (15 sentences):

| Airport | Sentence |
|---|---|
| KABQ | HAZARDOUS WX IN THE ABQ AREA CONTACT FSS FOR DETAILS. |
| KBOS | HAZD WX INFO FOR NEW ENGLAND AREA AVAILABLE ON FSS FREQS. |
| KCHS | ATTN ALL ACFT HAZARDOUS WX INFO CNTR WX ADVZY FOR THE CHS AREA ON FLT SVC FREQ. |
| KDJT | HAZD WX INFO AVBL FM IN FLIGHT OR FSS. |
| KFLL | CTC FSS FOR INFO. |
| KHPN | HAZD WX INFO FOR HPN AREA AVAILABLE ON FSS FREQ. |
| KLAX | HAZD WX INFO FOR LAX AREA AVBL FM FSS. |
| KMIA (arr and dep) | HAZD WX INFO FOR MIA AREA AVBL FSS. |
| KOKC | HAZD WX INFO FOR OKC AREA AVBL FM FLT WATCH OR FSS. |
| KONT | HAZD WX INFO FOR SOUTHERN CA AVBL ON FSS FREQUENCIES. |
| KPVD | HAZD WX INFO FOR PVD AREA AVBL FM FSS. |
| KSEA | HAZD WX INFO FOR WESTERN WA CONTACT FLIGHT SERVICE. |
| KTEB | HAZ WX INFO FOR TEB AREA AVBL FM MIV AFSS ON 122.2. |
| PHNL | MORE INFO AT HNL AFSS. |

Limits:
- The keywords must appear. A sentence that only says `FLT SVC` (as opposed to `FLIGHT SERVICE`) is not caught on its
  own. KCHS is removed only because it also says `HAZARDOUS WX`.
- KTEB's sentence includes a frequency (`122.2`). It is removed with the rest, and the future frequency feature will
  not see it.

### `drone-laser-reports`

Removes sentences that mention the whole words `UAS`, `UAV`, `DRONE`, `DRONES` or `LASER`.

These are real-time sightings of real aircraft and lasers around a real airport, which have no counterpart on VATSIM.

Removed in the test capture (6 sentences):

| Airport | Sentence |
|---|---|
| KATL (arr and dep) | DRONE OVER TERMINALS B, C, AND D 200FT. |
| KDJT | UAS OPERATING SOUTH OF AIRPORT AT OR BELOW 600 FT. |
| KMEM | UNAUTHORIZED LASER ILLUMINATION EVENT, AT 1012Z, 5 MILES SOUTH WEST OF MEM, TEN THOUSAND FEET, GREEN LASER. |
| KONT | UAS IN THE VICINITY OF ONT AOB 200 FEET. |
| KPVD | DRONE 1 NM NW OF THE FIELD AT OR ABOVE 300 FT AGL. |

Limits: other wordings, such as `UNMANNED AIRCRAFT`, are not matched.

### `tfr-notam-pointers`

Two patterns, in this order:

1. `CHECK NOTAMS` (optionally followed by `FOR ADNL INFO`), together with the whitespace before it, but only when it is
   followed by `...`. This is the pointer that tells pilots to read the NOTAMs.
2. Any sentence containing `TFR IN EFFECT`.

The first pattern exists because of how the NOTAM heading is normalized. The text `CHECK NOTAMS.` becomes
`CHECK NOTAMS...`, which looks exactly like the NOTAM heading and would make the split in step 6 land in the wrong
place. Removing the pointer first keeps the split where it belongs. Removing a sentence that contains only that
pointer would glue its neighbours together and break the split (leaving `NOTAMS....`), so the pointer is removed
without taking the sentence with it.

Removed in the test capture (2 sentences):

| Airport | Sentence |
|---|---|
| KDJT | TFR IN EFFECT CHECK NOTAMS... |
| KFLL | TFR IN EFFECT FOR THE SOUTH FLORIDA AREA. |

Limits: only the exact phrase `TFR IN EFFECT` is matched. Other TFR wording is left alone.

## Airport rules

Rules for a single airport live in a JSON file. The built-in one ships empty. Yours lives at:

```
%APPDATA%\VatisSync\replacements.json
```

```json
{
  "replacements": {
    "KSFO": {
      "CLSD": "CLOSED",
      "CTC GC FOR PUSH": ""
    }
  }
}
```

- Keys are regular expressions. Values are what to put in their place. An empty value removes the match.
- Rules run in the order written. Your rules run after the built-in file's rules for the same airport.
- In a replacement, `\1` refers to the first capture group. `%r` is removed, for compatibility with old rule files.
- Comments (`//`) and trailing commas are allowed.
- The file is re-read when it changes, so no restart is needed. If it is invalid, the last good copy is kept and a
  warning names the file.
- A pattern that does not compile is skipped, and one that takes more than a second is abandoned.
- The `{S}` shortcut is **not** available here. Write the full pattern.

Tip: match the text as it looks after step 3 above, in capitals, and remember the trailing punctuation is handled
for you.

## Deletion rules

If you delete a piece of text in vATIS, vATIS-Sync notices after the next refresh and removes the same words from
that station from then on. It compares words, not punctuation, and only acts on pure deletions. Edits and additions
are left alone. If you delete a contraction that vATIS inserted (for example `@TB`), the original words are
what gets removed.

## Frequencies (planned)

Real ATIS often tells pilots to contact a position on a frequency, and on VATSIM that controller may not be online.
A frequency feature is designed but not built. Each kind of position will get a mode:

- **Off**: keep the real-world text.
- **On**: remove the instruction.
- **Override**: replace the frequency with one you choose.

Replacement frequencies are always chosen by you. They are never guessed.

## Changing or adding built-in rules

Rules are tested against captured real data in `tests/VatisSync.Tests`. `BuiltInRulesTests` pins how many sentences
each rule removes from the 88-entry capture and checks that rules only remove text, never change it, and never break
the NOTAM split. After changing a rule, run `dotnet test` and read the sentences the test prints. The numbers should
change only when the rule starts touching different text, so check that before updating them.
