# VPS test brief: run 4 (OpenAI vs Soniox)

The app now has a second translation engine, **Soniox**, selectable next to OpenAI. The Soniox client was written
from Soniox's official SDKs and tested only against a local simulation. This run:

1. checks the Soniox protocol assumptions against the live service;
2. compares both engines **on the same audio files**;
3. checks that OpenAI still behaves as in run 3 (no regressions from the refactor).

You are working for the Claude session that wrote the app. Report evidence; **don't change app code**.

## Rules

- Read both keys without echoing them:
  `read -rs OPENAI_API_KEY && export OPENAI_API_KEY` and `read -rs SONIOX_API_KEY && export SONIOX_API_KEY`.
  Never print, save or commit them.
- Budget: **under $0.60** on OpenAI (about 8 minutes of audio). Soniox costs about $0.002 per minute, so its part is
  a few cents. Don't repeat runs.
- Commit only text. Before pushing, all three checks below must print nothing.
- Run the live steps **one at a time**, not in parallel.
- If a step fails: record the command and the exact error, try one reasonable fix, then move on.

## 0. Setup

```bash
cd docs/discord-live-subtitles 2>/dev/null || { git clone https://github.com/Lubingi/docs.git && cd docs/discord-live-subtitles; }
git fetch origin && git checkout claude/discord-live-subtitles-obc4xl && git pull
git log --oneline -1
R=test-results/run4; mkdir -p $R
CLI="dotnet tools/LiveSubtitles.Cli/bin/Release/net10.0/livesubs-cli.dll"
dotnet build -c Release 2>&1 | tail -2
dotnet test tests/LiveSubtitles.Core.Tests -c Release 2>&1 | tail -1 | tee $R/unit-tests.txt   # expected: 102 passed
T=$(ls work2/dialogue-turkish-3sp-*.wav); N=$(ls work2/dialogue-norwegian-4sp-*.wav); E=$(ls work2/dialogue-english-2sp-*.wav)
```

Reuse run 2's audio in `work2/`. If it's gone, regenerate as in run 2 step 2 and say so in the summary.

## 1. Soniox raw probe (checks the assumptions)

```bash
$CLI probe-soniox "$T" $R/soniox-probe-tr.jsonl en | tee $R/soniox-probe-tr.txt
$CLI probe-soniox "$E" $R/soniox-probe-en.jsonl en | tee $R/soniox-probe-en.txt
```

If the first command reports an error:

- For an unknown model, retry once with the model id the error suggests, adding `--soniox-model <id>`. Use that id for
  all Soniox commands below (`translate … --soniox-model <id>`).
- For an auth or credit error, stop the Soniox part and report it.

From the `.jsonl` files (each line is one raw server message), answer each question with an example line:

1. Do **final original** tokens have `start_ms`/`end_ms`, and **translation** tokens (`translation_status:
   "translation"`) have none?
2. **Order:** in the stream of *final* tokens, is it always *original chunk → its translation → next original
   chunk*? Or can the originals of the next sentence become final before the previous translation is complete? Count
   any interleaving.
3. Do tokens carry `speaker`, and do the translation tokens carry the **same speaker** as their originals?
4. **English speech** (the EN file): what `translation_status` do its tokens have? Is any translation emitted for it?
5. **Latency:** for a few sentences, compare the `t_ms` when the last original word became final with its `end_ms`
   (take `sent_ms` into account: audio is sent in real time). Do the same for the end of its translation.
6. Are the `<end>` / `<fin>` tokens present, and do any other special tokens appear?

## 2. Both engines, same files

```bash
for eng in openai soniox; do
  $CLI translate "$T" en --engine $eng --report $R/report-tr-$eng.json | tee $R/translate-tr-$eng.txt
  $CLI translate "$N" en --engine $eng --report $R/report-no-$eng.json | tee $R/translate-no-$eng.txt
  $CLI translate "$E" en --engine $eng --report $R/report-en-$eng.json | tee $R/translate-en-$eng.txt
done
```

Make one table per file, with a column per engine:

- **Accuracy:** per line, correct / cross-speaker bleed / wrong / missing. Use the script files in `work2/`.
- **Line splits:** for turns that were split into two segments by a pause, is the text split sensibly?
- **Blank lines:** any line in `lines` with text in `Original` but an empty `Translation` and `SameLanguage: false`.
- **English lines** (EN file): the exact words, marked `SameLanguage: true`, and no translated paraphrase.
- **Latency:** median first text and median final (`segments[].FirstTextLatencyMs`, `FinalLatencyMs`), and
  `avgTextLagMs`.
- **Cost:** the `est. cost` line of each run.
- **Wording:** one or two examples where the engines translate the same line noticeably differently. Which is
  better?

## 3. Silence skipping with Soniox

```bash
$CLI translate work2/tr-gaps.wav en --engine soniox --report $R/report-tr-gaps-soniox.json | tee $R/translate-tr-gaps-soniox.txt
```

After each line, the app stops sending audio and asks Soniox to finalise. Check:

- Does every line's **last word and punctuation** arrive before the next speaker starts? Look for text of segment N
  arriving after segment N+1 began (`rawEvents`, `EN @…ms → seg N`).
- Is the final latency similar to step 2?
- Are there Soniox **timeouts** (error 408) or reconnects during the 8 s gaps? The app sends a keepalive every 4 s
  without audio.

## 4. Dropped connection with Soniox

```bash
$CLI translate "$N" en --engine soniox --drop-at 20 --report $R/report-no-drop20-soniox.json | tee $R/translate-no-drop20-soniox.txt
```

- Record `Connection dropped mid-speech; re-sending the last … ms`, and the reconnect time.
- Around the drop, compare the lines with the script and with `report-no-soniox.json`: are there duplicated,
  glued or missing words?

## 5. Deliver

Write `$R/SUMMARY.md` (under about 200 lines):

- **Setup:** OS, commit, whether the audio was reused, and the Soniox model id that worked.
- **Step 1:** the six answers, with example lines. Say plainly where an assumption is wrong. This matters most.
- **Step 2:** the comparison tables and a short verdict: which engine is better for this app, on quality,
  latency and cost?
- **Steps 3–4:** findings.
- **New problems:** with evidence and a suggested fix.
- **Cost:** the estimate for each engine.

Then check for keys and push:

```bash
grep -rn "sk-" test-results/ && echo "OPENAI KEY FOUND - STOP"
grep -rnF "$OPENAI_API_KEY" test-results/ >/dev/null && echo "OPENAI KEY FOUND - STOP"
grep -rnF "$SONIOX_API_KEY" test-results/ >/dev/null && echo "SONIOX KEY FOUND - STOP"
git checkout -b vps-test-results-run4 && git add test-results/run4 && git commit -m "VPS test results run 4"
git push -u origin vps-test-results-run4
```

If the push fails, print `SUMMARY.md` in full and make `test-results-run4.tar.gz` (text only).

Finish with one paragraph for the user: whether Soniox works, how it compares with OpenAI, what's wrong, and
whether the results were pushed.
