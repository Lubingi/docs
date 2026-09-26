# Test brief for a Claude session on the VPS

You are testing **Live Subtitles for Discord** (this folder) on behalf of the Claude session that wrote it. That
session could not reach OpenAI's documentation, had no OpenAI API key and could not run Windows. Your job is to
fill those gaps and **report back evidence**.

**Do not change application code.** If you find a bug, describe it with evidence and a suggested fix in your
report. The author will make the fix.

## Rules

- The OpenAI key comes from the user, typed into the shell only. Use `read -rs OPENAI_API_KEY && export OPENAI_API_KEY`.
  Never echo it, never write it to a file, never commit it. Before pushing anything, run
  `grep -rn "sk-" test-results/ && echo "KEY FOUND - STOP"` and make sure nothing matches.
- Budget: the whole brief should cost well under **$1** of OpenAI usage. Don't loop or repeat runs beyond what's
  listed here.
- Never commit audio (`*.wav`, `*.mp3`, `*.m4a`). Only commit text results.
- If a step fails, record the exact command and error, try one reasonable fix (for example, install a missing
  tool), and move on. Don't get stuck.

## 0. Setup

```bash
git clone https://github.com/Lubingi/docs.git && cd docs
git checkout claude/discord-live-subtitles-obc4xl
cd discord-live-subtitles
uname -a; cat /etc/os-release 2>/dev/null | head -2      # record the OS
```

Install the **.NET 10 SDK**:

- Ubuntu 24.04: `sudo apt-get install -y dotnet-sdk-10.0`
- Otherwise, use Microsoft's install script: <https://dot.net/v1/dotnet-install.sh> with `--channel 10.0`

Also install `ffmpeg`. `yt-dlp` is optional (`pipx install yt-dlp` or `pip install yt-dlp`).

```bash
mkdir -p test-results/run1 work
dotnet build -c Release 2>&1 | tail -3            # the first build downloads the models; a SHA-256 check guards them
CLI="dotnet tools/LiveSubtitles.Cli/bin/Release/net10.0/livesubs-cli.dll"
```

## 1. Unit and integration tests (no key needed)

```bash
dotnet test tests/LiveSubtitles.Core.Tests -c Release --logger "console;verbosity=normal" 2>&1 | tee test-results/run1/unit-tests.txt | tail -5
```

Expected: 49 passed.

## 2. Check the OpenAI documentation (no key needed)

Read these pages in full and follow any links to the Realtime translation API reference or events pages:

- <https://developers.openai.com/api/docs/guides/realtime-translation>
- <https://developers.openai.com/api/docs/models/gpt-realtime-translate>
- <https://developers.openai.com/api/docs/models/gpt-realtime-whisper>
- The Realtime API reference sections for translation sessions (client and server events)
- The pricing page for these two models

Write `test-results/run1/docs-findings.md`. For every question below, give the answer, a **verbatim quote**, and the
**URL**. If the docs don't say, write "not documented".

1. **Endpoints:** are the WebSocket URL and auth exactly
   `wss://api.openai.com/v1/realtime/translations?model=gpt-realtime-translate` with `Authorization: Bearer`?
   Is any other header required, such as `OpenAI-Beta`?
2. **Events:** list every client and server event for translation sessions. The app handles these:

   | Direction | Events |
   |---|---|
   | Client | `session.update`, `session.input_audio_buffer.append`, `session.close` |
   | Server | `session.created`, `session.updated`, `session.closed`, `session.input_transcript.delta`, `session.output_transcript.delta`, `session.output_audio.delta`, `error` |

   Are there any others, for example a `*.done`/completed event, rate-limit events, or segment/turn events?
3. **Output audio:** can translated audio output be disabled (a modalities or output-audio setting)?
4. **`elapsed_ms`:** what exactly does it mean on output and input transcript deltas? Is it the input-audio time
   the text corresponds to, or the time at which it was emitted?
5. **Session limits:** what is the maximum session length or `expires_at`? What happens at expiry? Is there an
   idle timeout if no audio is sent for a while?
6. **Billing:** is it per minute of audio sent, per minute of connected session time, or tokens? Is
   `gpt-realtime-whisper` input transcription billed separately? What are the current prices?
7. **Prompts and glossaries:** is there any glossary, prompt or vocabulary support now?
8. **`output.language`:** what format does it take (`"en"`, or `"english"`)? What is the exact list of output
   languages?
9. **Input languages:** confirm that Turkish and Norwegian (Bokmål/Nynorsk) are supported input languages.
10. **Silence:** what guidance is there on sending silence, gaps, or chunk sizes?
11. **Noise reduction:** what are the allowed `noise_reduction` values and the default?
12. **Anything else** in the docs that contradicts `README.md` → "How it works".

## 3. Make test audio (needs the key)

```bash
read -rs OPENAI_API_KEY && export OPENAI_API_KEY
$CLI dialogue Turkish 3 work/ 2>&1 | tee test-results/run1/dialogue-tr.txt
$CLI dialogue Norwegian 4 work/ 2>&1 | tee test-results/run1/dialogue-no.txt
$CLI dialogue English 2 work/ --gpt 2>&1 | tee test-results/run1/dialogue-en.txt
cp work/*.txt test-results/run1/     # the scripts: ground truth of who said what
```

Optional, if `yt-dlp` works: get about 90 s of real Turkish speech with 2+ people (a podcast) and about 90 s of
Norwegian (e.g. an NRK podcast). Convert them to WAV and describe the source in your summary. Don't commit the
audio.

```bash
yt-dlp -x --audio-format wav -o "work/real-tr.%(ext)s" "<URL>"
ffmpeg -y -ss 60 -t 90 -i work/real-tr.wav -ac 1 -ar 16000 work/real-tr-90s.wav
```

## 4. Local speaker labelling vs ground truth (no key needed)

For each generated dialogue WAV, with and without `--call-quality`:

```bash
for f in work/dialogue-*.wav; do
  n=$(basename "$f" .wav)
  $CLI diarize "$f" > test-results/run1/diarize-$n.txt
  $CLI diarize "$f" --call-quality > test-results/run1/diarize-$n-callquality.txt
done
```

Then compare each segment with the script (`Speaker k (voice): text`). Segments are in script order, but check
by timing if they don't line up one-to-one. Make a table per file:

| Segment | Expected speaker | Assigned label |
|---|---|---|

Also report:

- The number of voices found vs the real number.
- The number of "?" segments.
- Any merges of two people or splits of one person.

## 5. Raw API behaviour probe (needs the key; about 3 minutes of audio)

```bash
$CLI probe work/dialogue-turkish-*.wav test-results/run1/probe-tr.jsonl
$CLI probe work/dialogue-norwegian-*.wav test-results/run1/probe-no.jsonl
$CLI probe work/dialogue-english-*.wav test-results/run1/probe-en.jsonl
```

From the jsonl files, report:

- **Events:** every distinct `type` seen, with counts. Were there any types not listed in step 2?
- **`elapsed_ms`:** is it present on output deltas? On input deltas?
- **Timing:** for output deltas, compute `sent_ms - elapsed_ms` at receipt and `t_ms - elapsed_ms`. Give the
  min, median and max. This tells us how `elapsed_ms` relates to audio time.
- **Lag:** how far output text lags the speech. Use the script plus the dialogue's timings where you can.
- **Session:** the `expires_at` in `session.created`, and the session JSON's defaults.
- **Closing:** after `client.close_sent`, did more text arrive, and did `session.closed` arrive? How long did it
  take?
- **English dialogue:** did the model output English text for English speech, or nothing? What did the input
  (original) transcript contain?
- **Punctuation:** do output deltas contain sentence punctuation (`.` `?` `!`)? The app uses it to split lines.

## 6. Full pipeline (needs the key; about 4 minutes of audio)

```bash
$CLI translate work/dialogue-turkish-*.wav en --report test-results/run1/report-tr.json | tee test-results/run1/translate-tr.txt
$CLI translate work/dialogue-norwegian-*.wav en --report test-results/run1/report-no.json | tee test-results/run1/translate-no.txt
$CLI translate work/dialogue-turkish-*.wav en --call-quality --report test-results/run1/report-tr-cq.json | tee test-results/run1/translate-tr-cq.txt
$CLI translate work/dialogue-english-*.wav en --report test-results/run1/report-en.json | tee test-results/run1/translate-en.txt
# optional: real podcast clips from step 3
```

For each run, judge:

1. Does each subtitle line contain the translation of the **right** script line, without text from the
   neighbouring line bleeding in? Count the correct, bled-into-next and bled-into-previous lines.
2. Is the speaker label right? Compare with the script.
3. Are the translations good? You can read Turkish and Norwegian, so rate them as good, minor issues or wrong.
4. From the `segments` in the report: the median first-text latency, the median final latency, and the average
   text lag.
5. How many minutes were sent vs the file's duration (the cost saving from not sending silence).

## 7. Only if this VPS runs Windows (skip on Linux)

```powershell
cd discord-live-subtitles
powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1      # needs Inno Setup 6: winget install JRSoftware.InnoSetup
$p = Start-Process artifacts\publish\LiveSubtitles.exe -ArgumentList '--smoke-test',"$PWD\smoke.txt" -PassThru; $null=$p.Handle; $p.WaitForExit(180000); Get-Content smoke.txt; $p.ExitCode
```

Copy `smoke.txt` and today's log from `%APPDATA%\LiveSubtitles\logs` into `test-results/run1/`.

If there's a desktop session with audio, also run the app itself. Use the Audio file source with the generated
Turkish dialogue and describe what the overlay shows (take screenshots if you can).

## 8. What to give back

Write `test-results/run1/SUMMARY.md`, keeping it under about 300 lines:

- **OS and SDK:** the OS, the .NET SDK version, and which steps ran or were skipped.
- **Unit tests:** the result.
- **Doc findings:** the short answers to the 12 questions in step 2 (the details go in `docs-findings.md`), and
  **any contradiction with how the app works, in bold**.
- **API probe:** your conclusions from step 5.
- **Pipeline:** accuracy tables and latency from step 6, plus the speaker accuracy from step 4.
- **Bugs and suggestions:** each with evidence (file, line, log excerpt) and a suggested fix. Don't change the
  app code.
- **Cost:** the total OpenAI cost of this test run (from platform.openai.com/usage if you can see it, otherwise
  an estimate).

Then deliver it:

```bash
grep -rn "sk-" test-results/ && echo "KEY FOUND - STOP"      # must print nothing
git checkout -b vps-test-results
git add test-results/
git commit -m "VPS test results run1"
git push -u origin vps-test-results
```

If you can't push, print `SUMMARY.md` in full at the end so the user can paste it back. Also make
`test-results.tar.gz` (text files only) for the user to send.

Finally, tell the user in one paragraph: what passed, what failed, and whether the results were pushed.
