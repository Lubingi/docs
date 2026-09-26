# VPS test results, run 1 (2026-09-26)

## OS, SDK and steps

- Ubuntu 24.04.5 LTS, kernel 6.8.0-138, x86_64. .NET SDK 10.0.112 (apt `dotnet-sdk-10.0`), ffmpeg 6.1.1, yt-dlp via pipx.
- Branch `claude/discord-live-subtitles-obc4xl` @ `66c56b5`.

| Step | Status |
|---|---|
| 0 Setup/build | **Failed first time** (bug 1, model SHA-256). Passed after putting the matching model in `models/`. |
| 1 Unit tests | Passed |
| 2 Docs | Done → `docs-findings.md` |
| 3 Test audio | Done: TR 3 speakers 55.5 s, NO 4 speakers 57.7 s, EN 2 speakers 62.2 s (`--gpt`). Optional real podcasts **skipped** to stay within budget. |
| 4 Diarization | Done |
| 5 Probe | Done |
| 6 Pipeline | **All 4 runs crashed on stop, and no report was written** (bug 2). Re-ran once with a test-only one-line patch in a scratch copy (see `patch-used-for-step6.diff`). The repo code is unchanged. Crash output is in `translate-*-crash.txt`. |
| 7 Windows | Skipped (Linux) |

## Unit tests

`Passed: 49, Total: 49` (`unit-tests.txt`).

## Doc findings (details, quotes and URLs are in `docs-findings.md`)

1. **Endpoint and auth:** `wss://api.openai.com/v1/realtime/translations?model=gpt-realtime-translate` + `Authorization: Bearer` is correct. No `OpenAI-Beta`. Examples add `OpenAI-Safety-Identifier` (optional).
2. **Events:** the documented set is exactly the app's 3 client and 7 server events. There are no done, turn or rate-limit events. The probe saw nothing else.
3. **Disabling output audio:** not possible (`session.update` only accepts output.language, input.transcription and input.noise_reduction).
4. **`elapsed_ms`:** docs: "alignment metadata … derived from the translation frame … advances in 200 ms increments". The probe shows it is the **input-audio position at emission** (see below), not the time of the words.
5. **Session limits:** not documented for translation. Realtime in general: 60 min. Measured: `expires_at` = creation + 60 min. Behaviour at expiry and any idle timeout: not documented.
6. **Billing:** by audio duration. translate $0.034/min, whisper $0.017/min. Whether whisper inside a translation session is billed extra is not documented. Assume yes ($0.051/min, as the CLI does).
7. **Prompts and glossaries:** not supported (cookbook).
8. **`output.language`:** a free string. Examples use ISO codes (`"es"`). The 13-language list (cookbook) matches `TranslationProtocol.OutputLanguages`.
9. **Input languages:** Turkish, "Norwegian" and "Nynorsk" are listed (cookbook only). Bokmål is not named separately.
10. **Silence:** "append audio in 200 ms chunks" and "**Keep appending silence while the session is active.** If a client stops sending audio and later resumes, model time treats the resumed audio as contiguous".
11. **Noise reduction:** `{type: "near_field" | "far_field"}` or `null`. The default is not documented. **Measured default: `null`** (and output language default: **`"es"`**, transcription `null`).
12. **Contradictions with the app:**
    - **The docs say three times (reference, guide checklist, cookbook) to stream continuously including silence. The app's default skips silence.** On these dense dialogues it saved almost nothing anyway (see below).
    - The docs say "Keep participant audio tracks separate". The app mixes Discord into one stream (a known design trade-off).
    - Everything else in README "How it works" is consistent with the docs.

## API probe (step 5), raw `probe` on the full files

- **Event types:** `session.created`, `session.updated`, `session.output_audio.delta`, `session.input_transcript.delta`, `session.output_transcript.delta`, `session.closed`. **No other types, and no `error`s.**

  | | TR | NO | EN |
  |---|---|---|---|
  | output_transcript.delta | 163 | 141 | 49 |
  | input_transcript.delta | 190 | 167 | 210 |
  | output_audio.delta | 230 | 217 | 197 |

- **`elapsed_ms`** is present on **every** output and input delta, and is always a multiple of 200.
- **Timing** (output deltas; min / median / max):

  | | TR | NO | EN |
  |---|---|---|---|
  | `sent_ms - elapsed_ms` | 200 / 200 / 400 | 200 / 200 / 400 | 200 / 200 / 400 |
  | `t_ms - elapsed_ms` | 69 / 92 / 288 | 129 / 191 / 378 | 94 / 101 / 325 |

  Input deltas: `sent_ms - elapsed_ms` = 200 / 400 / 800. **Conclusion:** `elapsed_ms` is "input audio received so far minus one frame" when the delta is emitted. It says **when** the text came out on the input clock, not **which** audio it translates. It is not word timing.
- **Lag** (TR probe, 12 lines, compared with the exact line timings recovered from the generator's zero-sample gaps): the translated line starts a median **~1.45 s** (0.7–3.5 s) after the speech starts. Its sentence end arrives a median **~1.05 s** (0.65–2.7 s) after the speech ends, in audio time; network adds about 0.1 s. So text for line N is often still arriving when line N+1 (another speaker) has started, because typical turn gaps are 0.25–0.9 s. This drives the bleed seen in step 6.
- **Session:** `expires_at` = creation + 60 min. `session.created` defaults: `{"noise_reduction":null,"transcription":null},"output":{"language":"es"}` plus an undocumented `"include":null`.
- **Closing:** after `client.close_sent`, **0** further transcript deltas (text was already drained). Only output audio kept coming. `session.closed` arrived after **4.8 s (TR) and 5.6 s (NO)**, and **never for EN** (the client gave up after 17 s while output-audio deltas were still arriving). Output audio is 1.4–1.7× longer than the input (TR: 92 s of audio for 55 s of input), so the server takes seconds to drain it. The app's 3 s stop / 5 s rotation waits will usually miss `session.closed`. That is harmless for text.
- **English speech → `en`:** in the probe, **no output text for the first 47 s**. After that the model paraphrased the last 3 lines into English ("Sounds perfect. I'll ping…"). In the step-6 run (same file, silence-gated, new session) it paraphrased **all** lines. So "does not translate same-language speech" is **inconsistent**. The input transcript was a correct English transcription throughout.
- **Punctuation:** yes. TR has 18 `.`, 3 `?`, 1 `!`; NO has 14 `.`, 5 `?`; EN has 6 `.`. But the final `.`/`?` of a sentence often arrives as **its own delta 200–400 ms later** (see bug 4).
- Translation quality in the probe (continuous audio) was good. The line breaks follow the sentences well.

## Speaker labels (step 4, `diarize`)

The ground truth is the script plus exact line times from the zero-sample gaps (`DialogueGenerator.cs:122`). The label is the streaming label printed per segment. Per-segment tables are in `diarize-*.txt`, aligned below.

| File | Voices found / real | Correct | "?" | Wrong | Merges/splits |
|---|---|---|---|---|---|
| TR | 3 / 3 | 12/13 | 1 | 0 | none (all "?" resolved retroactively) |
| TR call-quality | 3 / 3 | 10/12 | 2 | 0 | none |
| NO | 3 / 4 | 11/20 | 5 | 4 | **coral merged into marin** ("Speaker 3") |
| NO call-quality | 3 / 4 | 13/20 | 3 | 4 | **coral merged into marin** ("Speaker 2") |
| EN | **1 / 2** | 4/11 | 1 | 4 (+2 two-speaker segments) | **everything merged into Speaker 1** |
| EN call-quality | **1 / 2** | 5/12 | 1 | 5 (+1 two-speaker segment) | same |

TR segment → line (no CQ): 1=T1 S1 ✓, 2=T2 ? (marin), 3=T3 S1 ✓, 4–5=T4 S2 ✓ (ash), 6=T5 S3 ✓ (marin), 7=T6 S1 ✓, 8–9=T7 S1 ✓, 10=T8 S2 ✓, 11=T9 S3 ✓, 12=T10+T11 S1 ✓ (same speaker), 13=T12 S2 ✓.

NO segment → line (no CQ): 1 N1 S1 ✓ · 2 N2 ? · 3 N3 ? · 4–5 N3 S1 ✓ · 6 N4 ? · 7 N4 S2 ✓ · 8 N5 ? · 9 N5 S3 ✓ · **10–11 N6 (coral) S3 ✗** · 12 N7 S1 ✓ · 13–14 N8 S2 ✓ · **15–16 N9 (coral) S3 ✗** · 17 N10 ? · 18 N10 S3 ✓ · 19 N11 S1 ✓ · 20 N12 S2 ✓.

**EN root cause (bug 3):**
1. Segment 1 = L1 (cedar) + L2 (marin), and segment 2 = L3 + L4. The turn gaps were 0.36 s and 0.41 s, below `MinSilenceMs = 480`.
2. The first profile is therefore a blend of both voices, and it then matches both at 0.56–0.71.

Proof (local, no key needed):
- The same file trimmed to start at line 5, where every segment holds one voice: **2/2 voices, 8/8 correct**.
- Trimmed to start at line 2: one clean marin profile, which then absorbs the mixed L3+L4 segment (sim 0.57), and everything collapses again.

## Full pipeline (step 6), patched scratch build, text judged against the script

Legend: "→next" = this segment's text appeared in the following segment. "←prev" = a segment that received the previous segment's text. "stray" = starts with the previous sentence's `.`/`?`.

| Run | Segments | Correct text | →next | ←prev | Wrong | Stray punct. | Speaker correct / ? / wrong | Translation quality |
|---|---|---|---|---|---|---|---|---|
| TR | 13 | 10 | 1 ("Elif") | 1 | 1 (reconnect, bug 5) | 1 | 12 / 1 / 0 | good. Seg 9 "watch" for *oynarız* = "play" (minor). Seg 10 garbled. |
| TR call-quality | 12 | 12 | 0 | 0 | 0 | 4 | 10 / 2 / 0 | good |
| NO | 20 | 10 | 5 | 5 | 1 ("Enig." → "Right, **Emil**?") | 0 | 11 / 5 / 4 (coral) | mostly good. "Forrige gang" → "earlier" is fine. |
| EN | 11 | 5 | 3 | 3 | 0 | 3 | 4 / 1 / 4 + 2 mixed | English paraphrased: "Yo Alex" → "Yeah, folks, Alex", "vibing" → "feeling it", "Sounds perfect" dropped |

Bleed is worst on short segments (NO has many 1–2 s segments, split mid-sentence).

Examples:
- NO seg 6 ("Jeg er med, men jeg må") shows only "?".
- NO seg 7 shows the whole sentence.
- NO seg 18/19: "I can keep up on the mapand" / "let you know. Perfect…" hands the end of marin's line to cedar's segment.

The **original-language column was split correctly** in almost every case. The input transcript lags less than the output.

Latency and cost (from `report-*.json` → `segments`):

| Run | median first text | median final | avg text lag | audio sent / file |
|---|---|---|---|---|
| TR | 2016 ms | 2530 ms | 427 ms | 55.1 / 55.5 s |
| TR call-quality | 2244 ms | 2872 ms | 469 ms | 55.2 / 55.5 s |
| NO | 1837 ms | 2419 ms | 406 ms | 56.6 / 57.7 s |
| EN | 1765 ms | 2279 ms | 405 ms | 62.0 / 62.2 s |

Silence gating saved only **0.4–2 %** on these dialogues, which have short gaps. Real calls will save more, but the saving is paid for with the documented "don't pause the stream" behaviour.

## Bugs and suggestions (no app code changed)

1. **The build fails on a clean machine: Silero VAD hash mismatch.** `build/Models.targets:11-12` downloads from tag `v5.1.2`, which hashes `2623A2953F6FF3D2C1E61740C6CDB7168133479B267DFEF114A4A3CC5BDD788F`. The pinned `1A153A22…88E3` is the file on **`master`** (verified with curl + sha256sum for v5.1.2, v5.1 and master). **Fix:** pin the URL to the commit whose file matches `1A15…`, or update the hash to `2623…` (and check that the code works with the v5.1.2 model). Workaround used: copy master's file to `models/silero_vad.onnx`.
2. **Crash on every stop: `ObjectDisposedException` (CancellationTokenSource disposed)** at `RealtimeTranslationSession.cs:183` ← `TranslationConnection.StopAsync` (`TranslationConnection.cs:329`) ← `SubtitlePipeline.StopAsync:137` (`translate-*-crash.txt`).
   - Sequence: `StopAsync` calls `CloseAsync` **before** `_cts.Cancel()` → the server closes → the run loop (ct not yet cancelled) sees `EndedTask` complete → disposes the session (`TranslationConnection.cs:201`) and logs **"Connection dropped (Connection ended) — reconnecting in 1s"** (line 209) → `StopAsync` disposes it again → `_cts.Cancel()` throws.
   - Effects: the CLI never writes `--report`. In the app, Stop probably throws too, and the user sees a false "Reconnecting" on every stop.
   - **Fix:** set a `_stopping` flag (or cancel the run loop) before `CloseAsync`, and make `RealtimeTranslationSession.DisposeAsync` idempotent (the test patch: `if (Interlocked.Exchange(ref _disposed, 1) != 0) return;`). "Stopped" is also raised twice.
3. **One two-speaker segment poisons the speaker profiles** (EN: 1 voice found for 2). The segmenter only splits on ≥480 ms pauses, but real and generated turn gaps are often 250–450 ms. The mixed embedding becomes or joins a profile, which then matches both voices. **Fix ideas:**
   - Embed long segments (> ~3 s) in 1.5 s windows; if the windows disagree (cos < MatchThreshold), split at the change point, or at least don't learn from the segment and mark it "?".
   - Never start a new speaker from a segment whose halves disagree.
   - Consider `MinSilenceMs` ≈ 250–300 ms.
   - Separately, the coral and marin voices merge in NO (both runs). This may just be CAM++ on TTS voices; worth checking with real voices.
4. **Text bleeds between segments and punctuation strays.** Output text trails speech by about 1–1.5 s (step 5), so the end of line N is often mapped to segment N+1. The most common symptom is N+1 starting with N's final `.`/`?` (8 lines in 4 runs). **Fix ideas:**
   - If a delta is only punctuation, or begins with `[.?!,;:]`, and the previous segment has no sentence end yet, append it to the previous segment.
   - More generally, keep output text in the previous segment until its sentence ends, when that happens within ~2 s of the previous segment's end.
   - The input transcript is aligned better. It could anchor this (count the sentences in the original of each segment).
5. **Speech is lost on an unexpected drop.** In the TR run the server closed without a handshake at ~33.6 s (no `error` event; the only one in 11 sessions). After the reconnect, the source transcript resumes at "Çok…". "kaybetmeyelim" (~33.0–34.7 s) is gone, and the translation reads "But theyA very frustrating day." The backlog (`MaxBacklog`) only holds audio captured after the drop is noticed; audio already sent to the dead socket is lost. **Fix:** keep a ring buffer of the last ~3 s sent and, on an unexpected drop, replay from the start of the current segment (or the last finalized text) into the new session.
6. **False "same language" on bled lines.** `SubtitlePipeline.cs:546` sets `sameLanguage` whenever the translation is empty. NO seg 8 "Samme her." and EN seg 9 "Sweet." were flagged `SameLanguage: true` only because their text bled into the next segment. With the Hide setting these lines would disappear. **Fix:** require evidence first, e.g. no output deltas at all from segment start until segment end + ~2.5 s, or a segment ≥ 1.5 s long, or several same-language segments in a row.
7. **Glued words come from the model, not the app:** raw deltas `" it"`, `"if"` → "itif". Same for "mapand" and "theyA". The docs say not to insert spaces, so this is only worth a heuristic if it's common (a letter-to-letter join where the previous delta ended a complete word). Low priority.
8. Minor:
   - The `DialogueGenerator.cs:105` `Speaker % speakers` folds the built-in script's 4th speaker onto Speaker 1. The TR 3-speaker script then has Speaker 1 answering themselves ("Sorun değil Elif…" right after their own line). Pick scripts per speaker count, or remap to the least-recent other speaker.
   - The `probe` CLI's `t_ms` clock restarts after `session.created` (`client.connected` 1444 → `session.updated` 117).
   - `translate` waits a fixed 8 s after the file ends. That's fine.
9. **Docs deviation to decide on:** silence skipping (see Doc findings, point 12). The dialogues can't measure its effect, because they have almost no silence. A test with real calls containing long pauses (for example a 5-minute recording with 3–10 s gaps, gated vs "Send continuously") would show whether the model's "contiguous time" hurts line splitting or quality.

## Cost

The usage dashboard was not visible, so this is an estimate at $0.051/min (translate + whisper):

| Item | Cost |
|---|---|
| Probe: 3.23 min | ≈ $0.16 |
| Translate: 8 runs (4 crashed + 4 re-runs), ≈ 7.6 min | ≈ $0.39 |
| TTS (gpt-4o-mini-tts, ~3 min of audio) + one gpt-5-mini script | ≈ $0.05 |
| **Total** | **≈ $0.60** (under $1) |

If whisper is not billed separately in translation sessions, the total is ≈ $0.40.
