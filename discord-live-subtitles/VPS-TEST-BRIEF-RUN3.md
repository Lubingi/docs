# VPS test brief: run 3

Run 2 (`test-results/run2/SUMMARY.md`) left open items N1–N8. They are now fixed. This run verifies them live and
compares with run 2 **on the same audio files**. You are working for the Claude session that wrote the app. Report
evidence; **don't change app code**.

## Rules

- Read the key with `read -rs OPENAI_API_KEY && export OPENAI_API_KEY`. Never print, save or commit it.
- Budget: **under $1**. The plan below is about 9 minutes of translated audio (≈ $0.50). Don't repeat runs.
- Commit only text. Before pushing, `grep -rn "sk-" test-results/` must print nothing.
- Run the live steps **one at a time**, not in parallel, so a drop in one run doesn't overlap another.
- Record every server drop you see: time, run, and whether the no-app `probe` also drops.
- If a step fails: record the command and error, try one reasonable fix, then move on.

## 0. Setup

```bash
cd docs/discord-live-subtitles 2>/dev/null || { git clone https://github.com/Lubingi/docs.git && cd docs/discord-live-subtitles; }
git fetch origin && git checkout claude/discord-live-subtitles-obc4xl && git pull
git log --oneline -1
R=test-results/run3; mkdir -p $R
CLI="dotnet tools/LiveSubtitles.Cli/bin/Release/net10.0/livesubs-cli.dll"
```

**N1, clean build:** it must pass on the **first** try.

```bash
rm -rf models/*.onnx; find . -name bin -o -name obj | xargs rm -rf
dotnet build -c Release 2>&1 | tee $R/clean-build.txt | tail -3
dotnet test tests/LiveSubtitles.Core.Tests -c Release 2>&1 | tail -1 | tee $R/unit-tests.txt   # expected: 92 passed
```

**Audio:** reuse run 2's files so the results compare directly.

```bash
ls work2/*.wav        # dialogue-turkish-3sp-*, dialogue-norwegian-4sp-*, dialogue-english-2sp-*, dialogue-turkish-2sp-*, tr-gaps.wav, no-gaps.wav
```

If `work2/` is gone, regenerate as in run 2 step 2 and step 5 (`spread … 8`). Then say in the summary that the
audio is new.

## 1. Speaker labels (N6/N7, free)

```bash
for f in work2/dialogue-*.wav; do n=$(basename "$f" .wav)
  $CLI diarize "$f" > $R/diarize-$n.txt
  $CLI diarize "$f" --call-quality > $R/diarize-$n-callquality.txt
done
```

The output now ends with `final labels per segment`, which includes the retroactive fixes. Use those final
labels for the table (voices found / real, correct, "?", wrong, merges), side by side with run 2.

Targets:

- the "?" count goes down clearly (run 2: TR 8/24, NO 13/26), with **no increase in wrong labels**;
- EN still finds 2/2 voices with 0 wrong;
- the first voice exists by about the second short phrase (run 2 TR: not until 8.9 s).

## 2. Full pipeline on the run-2 files (N2, N4, latency)

```bash
T=$(ls work2/dialogue-turkish-3sp-*.wav); N=$(ls work2/dialogue-norwegian-4sp-*.wav); E=$(ls work2/dialogue-english-2sp-*.wav)
$CLI translate "$T" en --report $R/report-tr.json | tee $R/translate-tr.txt
$CLI translate "$N" en --report $R/report-no.json | tee $R/translate-no.txt
$CLI translate "$E" en --report $R/report-en.json | tee $R/translate-en.txt
```

Report:

- **The run-1/run-2 table:** correct / cross-speaker bleed / wrong / stray punctuation / speaker labels, run 2 vs run 3.
- **N2:** no line with text in its original may be blank. Look for `DisplayText`-empty lines: `Translation` is
  empty and the line isn't SameLanguage. For EN specifically, check "Awesome!", "Totally."-style short turns.
- **Latency:** the median final latency should be back near run 1's (2.3–2.9 s; run 2 was 3.2–3.7 s). Also
  report the median first text, and whether `avg text lag` is now sane (a few hundred ms).
- **Connection time:** the CLI now warns when OpenAI takes more than 3 s to accept the connection. Note these.

## 3. Silence skipping and the original text (N3)

```bash
$CLI translate work2/tr-gaps.wav en --report $R/report-tr-gaps.json | tee $R/translate-tr-gaps.txt
$CLI translate work2/no-gaps.wav en --report $R/report-no-gaps.json | tee $R/translate-no-gaps.txt
```

This is the default mode only.

- **Original placement:** count segments whose **original** text sits on another speaker's line. Run 2 had TR 5,
  NO about 8; the target is about 0.
- **Translation quality:** does it still match run 2's default mode?
- **Lag metric:** `avg text lag` should now be about the same as in continuous mode (run 2: 834/557 ms), not
  3–8 s.

## 4. Dropped connection (N5)

```bash
$CLI translate "$N" en --drop-at 20 --report $R/report-no-drop20.json | tee $R/translate-no-drop20.txt
$CLI translate "$T" en --drop-at 9 --report $R/report-tr-drop9.json | tee $R/translate-tr-drop9.txt
```

The second drop lands mid-sentence.

- The output should now show `Info: Connection dropped mid-speech; re-sending the last … ms of audio` (N8). Record
  the ms.
- **Around the drop:** compare the affected lines with the script and with the no-drop runs in step 2.
  - Duplicated words ("II", "Bence Bence") should be gone.
  - Glued words ("butOn") should be gone.
  - The meaning should survive (run 2: "…eat dinner first" became "On a lunch break").
- Also check any **real** server drops the same way.

## 5. Deliver

Write `$R/SUMMARY.md` (under about 200 lines):

- **Setup:** OS, commit, and whether the audio was reused.
- **N1–N8 verdicts:** each one FIXED, IMPROVED or STILL BROKEN, with evidence.
- **Tables:** run 2 vs run 3 for steps 1–3, plus the step 4 findings.
- **Server drops:** your observations.
- **New problems:** with evidence and a suggested fix.
- **Cost:** the total estimate.

Then push:

```bash
grep -rn "sk-" test-results/ && echo "KEY FOUND - STOP"
git checkout -b vps-test-results-run3 && git add test-results/run3 && git commit -m "VPS test results run 3"
git push -u origin vps-test-results-run3
```

If the push fails, print `SUMMARY.md` in full and make `test-results-run3.tar.gz` (text only).

Finish with one paragraph for the user: what's fixed, what's still wrong, and whether the results were pushed.
