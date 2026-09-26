# VPS test brief: run 2

Run 1 (results in `test-results/run1/`, start with `SUMMARY.md`) found 6 bugs. All six are now fixed. This run
**verifies the fixes against the live API** and answers one open question: does skipping silence hurt quality?
You are working for the Claude session that wrote the app. Report evidence; **don't change app code**.

## Rules

Same as run 1:

- Read the key with `read -rs OPENAI_API_KEY && export OPENAI_API_KEY`. Never print it, save it or commit it.
- Budget: **under $1.50**. The plan below is about 14 minutes of translated audio (≈ $0.75) plus text-to-speech.
  Don't repeat runs.
- Commit only text (no audio). Before pushing, `grep -rn "sk-" test-results/` must print nothing.
- If a step fails: record the command and error, try one reasonable fix, then move on.

## 0. Setup

```bash
cd docs 2>/dev/null || git clone https://github.com/Lubingi/docs.git && cd docs
git fetch origin && git checkout claude/discord-live-subtitles-obc4xl && git pull
cd discord-live-subtitles
git log --oneline -1                               # record the commit you tested
rm -f models/silero_vad.onnx                       # bug 1 check: the clean download must now pass its hash
dotnet build -c Release 2>&1 | tail -3
CLI="dotnet tools/LiveSubtitles.Cli/bin/Release/net10.0/livesubs-cli.dll"
R=test-results/run2; mkdir -p $R work2
```

## 1. Unit tests

```bash
dotnet test tests/LiveSubtitles.Core.Tests -c Release 2>&1 | tee $R/unit-tests.txt | tail -2
```

Expected: all pass (81 at the time of writing).

## 2. Fresh test audio

```bash
read -rs OPENAI_API_KEY && export OPENAI_API_KEY
$CLI dialogue Turkish 3 work2/ | tee $R/dialogue-tr.txt
$CLI dialogue Norwegian 4 work2/ | tee $R/dialogue-no.txt
$CLI dialogue English 2 work2/ --gpt | tee $R/dialogue-en.txt
$CLI dialogue Turkish 2 work2/ | tee $R/dialogue-tr2.txt
cp work2/*.txt $R/
```

For the 2-speaker Turkish script, check that no speaker answers themselves: consecutive lines only share a voice
where the original script has the same person twice.

## 3. Speaker labels, compared with run 1 step 4

```bash
for f in work2/dialogue-*.wav; do n=$(basename "$f" .wav)
  $CLI diarize "$f" > $R/diarize-$n.txt
  $CLI diarize "$f" --call-quality > $R/diarize-$n-callquality.txt
done
```

Make the same table as run 1 (voices found / real, correct, "?", wrong, merges/splits).

**The key check is bug 3:** the English 2-voice dialogue must find **2** voices, not 1.

## 4. Full pipeline, compared with run 1 step 6

```bash
$CLI translate work2/dialogue-turkish-3sp-*.wav en --report $R/report-tr.json | tee $R/translate-tr.txt
$CLI translate work2/dialogue-norwegian-*.wav en --report $R/report-no.json | tee $R/translate-no.txt
$CLI translate work2/dialogue-turkish-3sp-*.wav en --call-quality --report $R/report-tr-cq.json | tee $R/translate-tr-cq.txt
$CLI translate work2/dialogue-english-*.wav en --report $R/report-en.json | tee $R/translate-en.txt
```

Check each of these:

- **Bug 2:** no `ObjectDisposedException`, no "reconnecting" at the end, and every `--report` file is written.
- **Bug 4:**
  - Use the same accuracy table as run 1: correct / →next / ←prev / wrong / stray punctuation.
  - Count only **cross-speaker** bleed as an error. A pause-split inside one speaker's turn is fine if the text
    matches that segment's original.
  - Also compare the `lines` translation with its `RawOriginal` per segment: do the sentences line up?
- **Bug 6 / English:**
  - In `report-en.json`, English lines should have `SameLanguage: true` and show the speaker's exact words
    (from the original), not paraphrases.
  - No Norwegian or Turkish line should be marked `SameLanguage: true`.
- **Latency:** median first text, median final, and average text lag (as in run 1).
- The run-1 speaker issue where coral and marin merged in the Norwegian file: does it still happen?

## 5. NEW: Does skipping silence hurt? (default vs `--continuous`)

The app stops sending audio 3 s after someone stops talking. OpenAI's docs say to keep sending silence. Test both
on dialogues with long pauses:

```bash
T=$(ls work2/dialogue-turkish-3sp-*.wav); N=$(ls work2/dialogue-norwegian-*.wav)
$CLI spread "$T" work2/tr-gaps.wav 8     | tee $R/spread-tr.txt   # every pause between lines becomes 8 s
$CLI spread "$N" work2/no-gaps.wav 8     | tee $R/spread-no.txt
$CLI translate work2/tr-gaps.wav en --report $R/report-tr-gaps-default.json    | tee $R/translate-tr-gaps-default.txt
$CLI translate work2/tr-gaps.wav en --continuous --report $R/report-tr-gaps-continuous.json | tee $R/translate-tr-gaps-continuous.txt
$CLI translate work2/no-gaps.wav en --report $R/report-no-gaps-default.json    | tee $R/translate-no-gaps-default.txt
$CLI translate work2/no-gaps.wav en --continuous --report $R/report-no-gaps-continuous.json | tee $R/translate-no-gaps-continuous.txt
```

For each pair, report:

1. **Accuracy:** per line, is the translation correct and complete, and on the right line? Use the same table
   as step 4.
2. **Late endings:** did any line's last words or final punctuation arrive only when the *next* person started
   talking? That's the failure silence-skipping could cause. Look in the `rawEvents` (`EN @…ms → seg N`) for text
   of segment N arriving after segment N+1 began.
3. **Final latency:** the median time from the end of speech to the final line.
4. **Cost:** audio minutes sent (printed at the end of each run) vs file length.
5. **Translation quality:** any difference in wording or completeness between the two modes for the same line.

Finish with a verdict: is the quality difference worth the extra cost? Give numbers.

## 6. NEW: Live dropped-connection test (bug 5)

```bash
$CLI translate work2/dialogue-norwegian-*.wav en --drop-at 20 --report $R/report-no-drop.json | tee $R/translate-no-drop.txt
```

At 20 s this kills the socket mid-speech, like a network failure.

- Check the output for the drop, then "Connection dropped mid-speech; re-sending the last … ms", then a reconnect.
- Compare the line spoken around 20 s with the script and with `report-no.json` from step 4. It should be
  complete. Some duplicated words are acceptable; losing words is not.
- Report the reconnect time and how many ms were re-sent.

## 7. Deliver

Write `$R/SUMMARY.md` (under about 250 lines):

- **Setup:** OS, commit tested, and which steps ran.
- **Bug-by-bug verdict** (1–6) with evidence and a comparison to run 1: FIXED, IMPROVED or STILL BROKEN.
- **Tables for steps 3 and 4:** run 1 vs run 2 side by side.
- **Step 5:** the silence verdict with numbers.
- **Step 6:** the drop test result.
- **New problems:** any new bugs, with evidence and a suggested fix.
- **Cost:** the total cost estimate.

Then push:

```bash
grep -rn "sk-" test-results/ && echo "KEY FOUND - STOP"
git checkout -b vps-test-results-run2 && git add test-results/run2 && git commit -m "VPS test results run 2"
git push -u origin vps-test-results-run2
```

If the push fails, print `SUMMARY.md` in full and make `test-results-run2.tar.gz` (text only).

Finally, tell the user in one paragraph: what's fixed, what's still wrong, the silence verdict, and whether it was
pushed.
