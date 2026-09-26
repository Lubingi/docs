# VPS test results, run 2 (2026-09-26)

## Setup

- Ubuntu 24.04.5 LTS, x86_64, 6 cores. .NET SDK 10.0.112.
- Commit tested: `b8f4b27` (claude/discord-live-subtitles-obc4xl).
- The key was passed as an environment variable per command only. It was never written to disk or printed.
- **Every step (0–6) ran.** Extra runs:
  - one raw `probe` (1.1 min) to find out whether the connection drops came from the app;
  - step 5's four runs and step 6 ran concurrently to save wall time.
- Unit tests: **81/81 passed** (`unit-tests.txt`).
- Evidence files made from the reports and exact script-line timings (the generator's zero-sample gaps):
  - `analysis-segments.txt`: every segment with its true line and voice, label, SameLanguage flag, translation and original;
  - `analysis-late-endings.txt`;
  - `analysis-speakers.txt`;
  - `analysis-src-drift.txt`.

## Server drops, which affected every live step

- **25 unexpected drops** in about 16 min of streaming today ("The remote party closed the WebSocket connection
  without completing the close handshake", no `error` event first). Run 1 had 1 drop in 11 sessions.
- **Not caused by the app:**
  - The raw `probe` (no pipeline, continuous audio) also dropped at 44.9 s.
  - The two concurrent TR gap sessions dropped **at the same moment twice** (106465/106485 ms and 121638/121772 ms). That points to the network path or the server.
- The app recovered every time. Reconnect took 1.5–2.2 s.

| Run | Drops |
|---|---|
| step 4 | TR, TR-CQ, EN: 1 each; NO: 0 |
| step 5 NO | default 2, continuous 4 |
| step 5 TR | default 6, continuous 8 |
| step 6 | 1 real, in addition to the forced one |

So bug 5 got plenty of live testing, but the step 5 quality comparison is **confounded** (see below).

## Bug-by-bug verdict

| # | Bug | Verdict | Evidence |
|---|---|---|---|
| 1 | Model hash | **FIXED** (with a new build race) | The clean download from `v6.2` hashes `1A153A22…`, as pinned. **New:** a clean build fails 2 times out of 2 with "cannot access the file … being used by another process" (MSB3923/MSB4018). App, Tests and Cli each run `LiveSubsDownloadModels` in parallel into the same `models/`. A second `dotnet build` passes. See N1. |
| 2 | Crash on stop | **FIXED** | 0 exceptions in 9 `translate` runs. All 9 reports were written. No "reconnecting" at stop; the final state is "Stopped" once. |
| 3 | Two-speaker segment poisons profiles | **FIXED** (with side effects) | EN: **2/2 voices, 0 wrong, 0 two-voice segments** (run 1: 1/2, 4–5 wrong). The segmenter now splits at pauses of about 290 ms, which doubles the segment count. Side effects: more "?", and slow speaker bootstrap at the start (N6, N7). |
| 4 | Text bleeding into the next speaker | **MOSTLY FIXED** | Cross-speaker bleed in step 4: **2 lines in 4 runs (run 1: 12)**. Stray leading punctuation: **0 (run 1: 8)**. Text that arrives after the next speaker has started is now attributed correctly (`analysis-late-endings.txt`: 10–11 such segments per dense run, all on the right line). |
| 5 | Speech lost on a drop | **IMPROVED, not fixed** | Real drops mid-speech (TR 26.5 s, TR-CQ 20.2 s, EN 22.5 s): **no words lost**, but duplicated ("II might be…", "Bence Bence", "Let's do casual first" twice). Forced drop right after a sentence (step 6): **meaning lost** ("I'm in, butOn a lunch break." vs step 4's "…I have to eat dinner first."). Words are glued at every reconnect join (N5). |
| 6 | False "same language" / English | **FIXED for detected lines**, new gap | EN: 14/19 lines `SameLanguage: true` with the **exact words** (e.g. "Gotta eat first, but can hop on by eight fifteen."). TR/NO: **0 of 71 lines** flagged. But **5/19 EN lines are blank**: short turns "Maybe around eight?", "Awesome!", "Totally.", "playlists and snacks ready.", "LOL Perfect." are `SameLanguage: false` with an empty translation (N2). |

The generator issue from run 1 is also fixed: both Turkish scripts (3 and 2 speakers) have no one answering themselves.

## Step 3: speaker labels (`diarize`), run 1 → run 2

Streaming label per segment. "Correct" uses a majority one-to-one label→voice map (`analysis-speakers.txt`).

| File | Voices found / real | Correct | "?" | Wrong | Two-voice segs | Merges |
|---|---|---|---|---|---|---|
| TR 3sp | 3/3 → 3/3 | 12/13 → **14/24** | 1 → **8** | 0 → 2 | 0 → 0 | marin once labelled as cedar (seg 17–18) |
| TR 3sp CQ | 3/3 → 3/3 | 10/12 → 16/22 | 2 → 5 | 0 → 1 | 0 → 0 | |
| NO 4sp | 3/4 → 3/4 | 11/20 → **9/26** | 5 → **13** | 4 → 4 | 0 → 0 | **coral+marin still merged** |
| NO 4sp CQ | 3/4 → 3/4 | 13/20 → 13/27 | 3 → 10 | 4 → 4 | 0 → 0 | **coral+marin still merged** |
| EN 2sp | **1/2 → 2/2** | 4/11 → **16/19** | 1 → 3 | 4 → **0** | 2 → **0** | none |
| EN 2sp CQ | **1/2 → 2/2** | 5/12 → 16/19 | 1 → 3 | 5 → 0 | 1 → 0 | none |
| TR 2sp (new) | 2/2 | 18/23 | 5 | 0 | 0 | none |

Norwegian got worse on "?": cedar was recognised in **1 of 8** segments (final: Speaker 1 = 1 segment), and 13 of 26 stay "?".
In TR 3sp, segments 1–6 (0–7 s) are all "?" with `best 0.00`: no speaker exists until segment 7, because every early
segment is shorter than `MinNewSpeakerMs` (1.5 s).

## Step 4: full pipeline, run 1 → run 2

Lines = the 12 script lines. Run 1 counted segments; run 2 counts script lines and **cross-speaker** bleed only.

| Run | Correct | →next | ←prev | Wrong | Stray punct. | Speaker labels (segs) correct / ? / wrong | Translation |
|---|---|---|---|---|---|---|---|
| TR | 10/13 segs → **10/12 lines** | 1 → 1 | 1 → 1 | 1 → 0 | 1 → 0 | 12/1/0 → 16/6/1 | good. L10's "siz saldırıya geçin" → shown on L11 (marin): "You all go on the attack. Deal…" |
| TR CQ | 12/12 → **12/12** | 0 → 0 | 0 → 0 | 0 → 0 | 4 → **0** | 10/2/0 → 16/5/1 | good |
| NO | 10/20 → **10/12** | 5 → **0** | 5 → **0** | 1 → 1 | 0 → 0 | 11/5/4 → 9/13/4 | L5 wrong ("These meetings usually start at eight"; whisper heard "Vanja starter"). L10 drops "Enig." |
| EN | 5/11 → 12/12 words exact | 3 → 0 | 3 → 1* | 0 → 0 | 3 → 0 | 4/1/4 → 16/3/0 | exact original words (no paraphrase), but 5 short segments are blank |

\* EN seg 16 original "LOL Perfect." carries L11's "Perfect" (cedar) on L10 (marin).

**Translation vs `RawOriginal` inside a segment:** lines that split at a pause inside one turn often don't line up. The
translation half trails the original. For example, NO seg 17/18 have original "Husker dere den kampen?" / "Vi var så nære å
vinne." but translation "Do you remember" / "that match? We were so close…". Roughly half of the within-turn splits
in NO and TR are like this. The whole turn reads correctly, and nothing crosses speakers.

Latency (from `segments`):

| Run | median first text | median final | avg text lag |
|---|---|---|---|
| TR | 2016 → 3017 ms | 2530 → **3749** ms | 427 → 355 ms |
| TR CQ | 2244 → 2512 ms | 2872 → 3275 ms | 469 → 434 ms |
| NO | 1837 → 2783 ms | 2419 → 3202 ms | 406 → 475 ms |
| EN | 1765 → n/a (no model text) | 2279 → 2532 ms | 405 → 522 ms |

The final latency is **0.3–1.2 s slower** than in run 1. Part of the TR number is the slow start: segments 1–8 finalised
3.3–13.3 s late, and the first line printed at 14.7 s.

## Step 5: skipping silence (default) vs `--continuous`, with 8 s pauses

`spread`: TR 58.4 → 147.6 s (12 pauses; one is a 10 ms click at the start of L10, which is harmless). NO 57.0 → 138.8 s.

| | TR default | TR continuous | NO default | NO continuous |
|---|---|---|---|---|
| Server drops during the run | 6 | 8 | 2 | 4 |
| Lines good / minor / wrong | 8 / 3 / 1 | 9 / 3 / 0 | 7 / 4 / 1 | 5 / 2 / 5 |
| Cross-speaker **translation** bleed | 0 | 0 | 0 | 0 |
| Late endings, cross-speaker (text after next speaker began) | 0 | 1 | 1 (a lone ".") | 0 |
| **Original column on another speaker's line** | **5 segments** | 0 | **~8 segments** | 0 |
| Median final latency | 4109 ms | 3949 ms | 3473 ms | 3764 ms |
| avg text lag (app metric) | 3228 ms | 834 ms | 7789 ms | 557 ms |
| Audio sent / file length | **1.67 / 2.46 min** | 2.90 / 2.46 min | **1.51 / 2.31 min** | 2.53 / 2.31 min |
| Cost at $0.051/min | $0.085 | $0.148 | $0.077 | $0.129 |

Per-line notes (all in `analysis-segments.txt`):

- Almost every "wrong" or "minor" line sits right next to a drop. Examples:
  - TR default L8 "Çok sinir bozucuydu" → "This is how you do it." (drop at 90.5 s);
  - TR continuous L10 "Tamam" → "Hello, everyone." (drop at 121.8 s);
  - NO continuous L1 got no translation at all, and L4 became "Yeah, man,And I am withbut I have…".
- Some misreadings happen in both modes, so they are unrelated to silence. NO L5 "Samme her" was misheard in both ("This is Vara" / "A summit meeting") and in step 4 too.

**What silence skipping does do (a new finding): it shifts the source-language column.**
- In default mode, the input transcript's `elapsed_ms` falls about **+3 s further behind** the output's `elapsed_ms` with every skipped gap: +3.2, +6.2, …, +10.6 s by the end of NO (`analysis-src-drift.txt`).
- In continuous mode it stays within ±0.4 s.
- The app maps SRC deltas by `elapsed_ms`, so in default mode originals land one line late. Examples:
  - TR seg 7 (cedar) shows ash's "Bu akşam kaçta başlıyoruz?";
  - NO seg 22 (coral) shows "Enig. Jeg kan følge med på kartet og si ifra Perfekt.".
- The **translations** are not affected. Their text arrives within the 3 s tail, and no translated line ended late
  because of skipping.

**Verdict:**
- Keep silence skipping as the default. It sends **32–35 % less audio than real time**, and **40–42 % less than continuous** (continuous also re-sends audio after drops).
- Translation accuracy, cross-speaker bleed and final latency showed **no measurable penalty** (0 translated line endings arrived late because of skipping; median final 3.5–4.1 s in both modes).
- Continuous was not better (14 good lines vs 15 across both files), though the drops make this a weak comparison.
- The one real cost of skipping is the **misplaced original-language text**. That can be fixed in the app (N3) without sending silence.

## Step 6: forced drop at 20 s (NO)

- The drop was at 20.0 s, just after L4 (ash, 14.67–19.62 s).
- Reconnected in **1.48 s** (20075 → 21552 ms). A **real server drop** followed at 23.3 s, and that reconnect took 1.66 s.
- **How much was re-sent:** "Connection dropped mid-speech; re-sending the last … ms" is logged at Info level, and the CLI's `ConsoleLog` only prints Warning and above (`Program.cs:289`), so it doesn't appear. From `rawEvents`: the last text before the drop was at `elapsed 19000`, so the replay started at about 17.5 s. That is **about 2.5 s**: only "…spise middag først".
- **Result:** L4 = "I'm in, butOn a lunch break." (original "Jeg er med, men jeg må spise" | "middag said middag først."). In step 4, L4 was "I'm in, but I have to eat dinner first."
- **No words were lost from the original, but the translated meaning was.** Given only the tail, the model translated "middag først" as "On a lunch break". The join also glued "but" to "On".
- L5 ("Samme her. Hva med å starte klokka åtte?") became "SameYeah—once you hit start the clock starts at eight." because of the second drop mid-line.
- Everything after 27 s matches step 4, or is better.

## New problems (evidence and suggested fix)

- **N1. The clean build races on the model download.** `build/Models.targets` runs in 3 projects at once. Error: `MSB3923: Failed to download file … The process cannot access the file '…/models/silero_vad.onnx' because it is being used by another process`. Reproduced 2/2 after deleting `models/`.
  - Fix: download in one project only (e.g. `LiveSubtitles.Core`, which the others reference, so it builds first), and let the others just copy. Or download to a per-project temp file and move it into place.
- **N2. Short English turns are blank.** Five EN segments of 0.6–1.5 s (e.g. seg 3 "Maybe around eight?", seg 9 "Awesome!") have `SameLanguage: false` and `Translation: ""`, so no text is shown.
  - Fix: when a final segment has no translation and its original is non-empty, show the original when either (a) the local English check is inconclusive and the previous 2–3 segments were same-language, or (b) the segment is short. Never leave it blank.
- **N3. The original-language column is mis-mapped when silence is skipped.** The input transcript's `elapsed_ms` drifts about 3 s per gap relative to the output's (`analysis-src-drift.txt`), which puts originals on the next speaker's line (TR 5 segments, NO ~8).
  - Fix: map SRC deltas by **arrival time**, i.e. the session's `SentMs` at receipt minus ~300 ms (the input transcript trails the audio by 200–400 ms in both modes). Or re-base SRC `elapsed_ms` on the most recent output delta's.
- **N4. The avg text lag metric is wrong in skip mode:** 3228/7789 ms (default) vs 834/557 ms (continuous), with a similar final latency. It probably uses the same `elapsed_ms` mapping across skipped gaps. Fix it together with N3.
- **N5. Reconnect side effects.**
  - (a) **Words glued at every reconnect join**: "butOn", "SameYeah", "everyoneHello", "everyoneCan", "beSo", "withbut". The first delta of a new session has no leading space. Fix: when appending a new session's first delta to a line that already has text, insert a space if both sides are letters.
  - (b) **Duplicates** from the replay overlapping text already received ("II", "Bence Bence", "Let's do casual first" twice). Fix: drop new-session deltas mapped before the old session's last translated point, or trim a repeated prefix.
  - (c) **Lost context** (step 6). Fix: replay from the start of the last segment whose translation hasn't reached a sentence end (e.g. up to 6 s), instead of `LastOutputElapsed − 1.5 s`.
- **N6. Slow speaker bootstrap at the start.** TR step 4: the first 6 segments are shorter than `MinNewSpeakerMs` (1.5 s), so no speaker exists until 8.9 s, and the lines were only released at 14.7 s (final latency 13.3 s for seg 1).
  - Fix: allow the first speaker from ≥2 similar consecutive short segments totalling ≥1.5 s. Also don't hold "?" lines back from being shown.
- **N7. More "?" because segments are shorter.** Short segments give weak embeddings (NO: 13/26 "?", cedar recognised 1/8).
  - Fix: for identification, embed the run of consecutive segments that has no evidence of a speaker change (the bug-3 half-window check can decide), not each short piece.
- **N8. The CLI hides the replay message.** It's an Info-level log, and `ConsoleLog` shows Warning and above. Fix: print Info messages from `TranslationConnection` in the CLI, or add them to `rawEvents`.
- **Still present from run 1:** coral and marin merge in NO (diarize and pipeline, both runs). The model still glues some words itself ("mapand").

## Cost

The usage dashboard was not visible, so this is an estimate at $0.051/min.

| Item | Audio sent | Cost |
|---|---|---|
| Step 4 | 4.10 min | |
| Probe | 1.07 min | |
| Step 5 | 8.61 min | |
| Step 6 | 1.03 min | |
| **Realtime subtotal** | **14.8 min** | **≈ $0.76** |
| TTS: 4 dialogues, ~4 min gpt-4o-mini-tts, plus one gpt-5-mini script | | ≈ $0.07 |
| **Total** | | **≈ $0.83** (under $1.50) |

Drops and replays account for about 0.4 min of the audio sent.
