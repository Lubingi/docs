# Live Subtitles for Discord

A Windows desktop app that shows live English subtitles for Discord voice calls. It runs entirely on your PC:
no bots, no servers, nothing for the people you talk to to install. The translation itself comes from one of two
cloud services, your choice:

- **OpenAI** Realtime Translation (`gpt-realtime-translate`), about $2–3 per hour of speech;
- **Soniox** real-time translation, about $0.15 per hour of speech (see [Choosing a translation service](#choosing-a-translation-service)).

- Captures **only Discord's audio**, using Windows per-app loopback capture. Your microphone, games and
  music are excluded.
- A local voice detector (Silero VAD) means only speech is sent for translation, not silence.
- The overlay is transparent and always on top. It streams partial text, then replaces it with the final line.
- Shows the original-language transcript under each subtitle (optional).
- **Who is speaking**, fully automatic and local. Each voice gets a label and a colour. Rename a label once and
  that voice is recognised in future calls. Mute English speakers so their speech isn't sent at all.
- Test mode: use any app (e.g. a YouTube video in Chrome) or a local audio file as the source, or generate a
  fake multi-speaker conversation with OpenAI text-to-speech.
- Debug panel: speech segments, latency and raw text.

All five build stages are complete. To try it quickly, see [Install](#install) and [Quick start](#quick-start).
[Testing each stage](#testing-each-stage) explains how to test every feature without other people.

## Requirements

- Windows 10 version 2004 (build 19041) or newer, or Windows 11. Per-app capture needs 2004+. On older builds
  the app falls back to capturing a whole output device.
- An API key for the service you use: OpenAI with access to `gpt-realtime-translate`, or Soniox (see below).
  The test-dialogue generator always uses OpenAI text-to-speech.
- For games: run the game in **borderless windowed** or windowed mode. Exclusive fullscreen draws over every
  overlay.

## Install

**Option A: download the installer.** Every push to this folder builds it on GitHub:

1. Open the repository on GitHub → **Actions** → **Live Subtitles (Windows app)**, and click the latest green run.
2. Under **Artifacts**, download **LiveSubtitlesSetup** (a zip containing `LiveSubtitlesSetup-<version>.exe`).
3. Run it. It installs for your user only, with no admin rights, into `%LOCALAPPDATA%\Programs\LiveSubtitles`.
   It includes the .NET runtime and both models, so nothing else is needed.

The installer isn't code-signed, so Windows SmartScreen may say *"Windows protected your PC"*. Click
**More info → Run anyway**. Uninstall from *Settings → Apps*. The uninstaller asks whether to also delete your
settings, voice profiles, logs and the saved API key.

**Option B: build the installer yourself.** With NSIS, which works on Linux too (`apt install nsis`) or
Windows (`winget install NSIS.NSIS`, then use Git Bash), run `installer/build-installer-nsis.sh`. Or with Inno Setup: install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
and Inno Setup 6 (`winget install JRSoftware.InnoSetup`), then run:

```powershell
powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1
```

The script runs the tests, publishes a self-contained build and writes
`installer\Output\LiveSubtitlesSetup-<version>.exe`.

## Quick start

1. Start **Live Subtitles**. Under **Translation service** pick OpenAI or Soniox, click **Set API key…**, paste
   that service's key, then **Test key** and **Save**.
2. Join a Discord voice channel. Keep **Audio source = Discord** and click **▶ Start** (or press `Ctrl+Alt+S`).
3. Subtitles appear at the bottom of the screen. To move the overlay, untick **Click-through** (`Ctrl+Alt+T`),
   drag it, then lock it again so clicks pass through to your game.
4. Click a speaker's name to give them a real name. They are recognised automatically next time.

## Getting an OpenAI API key

1. Sign in at <https://platform.openai.com/> (create an account if needed).
2. Add a payment method or credit under **Settings → Billing**. The Realtime API is pay-as-you-go.
3. Go to **API keys** (<https://platform.openai.com/api-keys>), click **Create new secret key**, and copy it.
   Keys start with `sk-`.
4. In the app, click **Set API key…**, paste the key, and click **Test key**, then **Save**.
   The key is stored in **Windows Credential Manager** (encrypted for your Windows account). It is never written
   to a file. To remove it, use **Remove saved key** in the same dialog, or delete
   `LiveSubtitles/OpenAI-API-Key` in *Control Panel → Credential Manager → Windows Credentials*.

**Cost:** `gpt-realtime-translate` is billed at about **$0.034 per minute** of audio sent. The optional
original-language transcript (`gpt-realtime-whisper`) adds about **$0.017 per minute**. Only detected speech,
plus a short tail after it, is sent.

## Choosing a translation service

Pick the service on the **Session** tab under **Translation service**. Each has its own key, stored separately.
Everything else (capture, speaker labels, overlay, glossary, history) works the same with both.

| | OpenAI `gpt-realtime-translate` | Soniox real-time |
|---|---|---|
| Cost per hour of speech | about $2.04, or $3.06 with the original text | about $0.12–0.15, original text included |
| Original-language text | optional extra model | always included |
| Glossary | applied to the text afterwards | also sent to Soniox as context (names, preferred translations) |
| Word timing | none: text is matched to speech by arrival time | every original word has its time, so lines split more precisely |
| Speech already in English | inconsistent (the app shows the original instead) | left untranslated; the app shows the original |
| Languages | 70+ spoken, 13 subtitle languages | 60+ spoken and subtitle languages |

Soniox is new in this app and has been tested against a local simulation of its protocol, not yet against the live
service. If a line looks wrong, the Debug tab shows the raw text for both services.

### Getting a Soniox API key

1. Sign up at <https://console.soniox.com/> and add credit under billing (pay-as-you-go).
2. Create an API key in the console and copy it.
3. In the app, choose **Soniox** under **Translation service**, click **Set API key…**, paste it, **Test key**, then **Save**.
   It is stored in Windows Credential Manager as `LiveSubtitles/Soniox-API-Key`.
4. Optional: under **Languages people speak**, enter two-letter codes such as `tr, no`. This helps accuracy;
   leave it empty to detect languages automatically.

## Building and running from source

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (x64). From this folder:

```powershell
dotnet build -c Release
dotnet run --project src/LiveSubtitles.App -c Release
```

The first build downloads the two local models into `models/` and checks their SHA-256 hashes:

| Model | Purpose | Size | Licence |
|---|---|---|---|
| Silero VAD v6.2 | Speech detection | 2.3 MB | MIT |
| 3D-Speaker CAM++ (VoxCeleb) | Speaker voice embeddings (stage 3) | 28 MB | Apache-2.0 |

Run the tests (they also run on Linux or macOS):

```powershell
dotnet test tests/LiveSubtitles.Core.Tests
```

## How it works

```
Discord.exe process tree ──(Windows process-loopback capture, 48 kHz)──┐
Any app / audio file / output device (test & fallback sources) ────────┤
                                                                        ▼
            resample → 16 kHz ──► Silero VAD ──► segmenter (splits on pauses)
                     → 24 kHz ─────────────────────────┘   │ speech + pre-roll + short tail only
                                                            ▼
               wss://api.openai.com/v1/realtime/translations?model=gpt-realtime-translate
               (session.input_audio_buffer.append, 200 ms PCM16 chunks)
                                                            │ session.output_transcript.delta (English)
                                                            │ session.input_transcript.delta  (original)
                                                            ▼
                  text is matched to speech segments by elapsed_ms → overlay + history
```

Facts about the Realtime Translation API that shaped the design. These were checked against OpenAI's
official SDK type definitions and the official cookbook guide for `gpt-realtime-translate`:

- Dedicated endpoint `/v1/realtime/translations`. Audio in is 24 kHz mono PCM16, base64-encoded, in
  200 ms chunks. The source language is detected automatically. Turkish, Norwegian and Nynorsk are among the
  70+ supported input languages. English is one of the 13 output languages.
- Output arrives as streamed `session.output_transcript.delta` events. The original-language
  `session.input_transcript.delta` events arrive only if input transcription (`gpt-realtime-whisper`) is enabled.
  **There are no "done" events** and no turns. `elapsed_ms` on each delta is the input-audio position *when the
  text was emitted*, not the time of the words. Measured on the live API, text trails speech by about 1–1.5 s, and a
  sentence's end arrives up to 2.7 s after the speaker stops. The app matches text to speech segments using:
  - that timing;
  - sentence punctuation, including a trailing `.` or `?` that arrives on its own after the next person has
    started;
  - the original-language transcript, which lines up better with the speech and says how many sentences each
    line should have.

  A line is final when the text has moved past it, or when nothing new arrives shortly after the speaker stops.
- Translated **audio cannot be switched off**. The app ignores `session.output_audio.delta`.
- The model **does not accept prompts or glossaries**, so the glossary is applied locally to the text (stage 2).
- For speech already in English, the model is inconsistent: in testing it produced nothing for 47 s, then
  paraphrased. So the app recognises English speech from the original transcript (a small local word check) and
  shows the speaker's exact words. See "Speech that is already in English" on the Session tab.
- OpenAI's docs say to keep sending audio, silence included ("model time treats the resumed audio as
  contiguous"). Sending everything costs $3/hour even when nobody talks. By default the app sends speech plus
  3 s of real silence after it, then stops until the next speech. The 3 s covers the up-to-2.7 s the model needs
  to finish a sentence. It also pads to a whole 200 ms frame. **Send continuously** (Settings) follows the docs
  exactly, at the higher cost.

**With Soniox** the flow is the same up to the segmenter. Audio goes as raw 24 kHz PCM16 binary frames to
`wss://stt-rt.soniox.com/transcribe-websocket`, after a JSON config with one-way translation to the subtitle language.
Soniox returns tokens: original words with their start and end times, followed by their translation (without times).
The app uses only final tokens. It places the original words on the line whose speech contains them, and gives each
translated word the time of the original word at the same position in its chunk. The start of each new translated
chunk also closes the previous line. When audio stops (silence skipping), the app asks Soniox to finalise the last
words at once instead of waiting.

## Privacy

- Audio is processed in memory and is **never saved to disk**. The exceptions are test files you choose or generate
  in test mode.
- Nothing is sent anywhere except the translation service you picked, OpenAI or Soniox (speech audio only, over
  TLS). With Soniox, the glossary terms are sent too, as context.
- The error log (`%APPDATA%\LiveSubtitles\logs`) contains connection events and errors. It never contains audio,
  transcript text or your key.

## Testing each stage

Everything can be tested without Turkish or Norwegian speakers, using test mode.

### Stage 1: capture, translation, overlay, debug panel

1. Start the app and set your API key (**Set API key…** → paste → **Test key** → **Save**).
2. **Any app (YouTube):**
   - Open Chrome/Edge and play a Turkish or Norwegian video. Good searches: "Türkçe podcast",
     "NRK nyheter", "Norsk podkast".
   - In the app, choose **Audio source → Any app**, click **Refresh**, and pick the browser (marked ♪ while it
     plays audio). Click **▶ Start**.
   - The overlay appears near the bottom of the screen. Grey italic text is partial. It turns solid white when
     the line is final, and the original Turkish/Norwegian appears underneath.
   - Pause the video: the lines fade out after about 8 s. Only the browser's audio is captured. Music from
     another app, or talking into your mic, has no effect.
3. **Audio file:**
   - Choose **Audio source → Audio file**, click **Browse…**, and pick an .mp3, .wav or .m4a. Download a Turkish
     or Norwegian clip, or record one from a video. Click **▶ Start**.
   - The file plays in real time (and through your speakers if that box is ticked).
   - Use ⏯ and the slider to pause and seek. Pausing flushes the current line.
4. **Discord:** join a voice channel (or play a YouTube video through Discord's "Watch Together") and use the
   default **Discord** source. Close and reopen Discord while running: the status line shows
   "Waiting for Discord…" and then re-attaches.
5. **Debug tab:**
   - One row per detected speech segment, with length, status (speaking → waiting for text → final), latency
     to first text and to the final line, and the raw translation and original text.
   - The event log shows each streamed delta and which segment it was matched to.
   - If lines are split oddly, the timings here show why.
6. **Overlay:** untick **Click-through** to drag the overlay by its handle or resize it from the corner. Tick it
   again to let clicks pass through to the game.

Headless check without the UI, on any OS: `dotnet run --project tools/LiveSubtitles.Cli -- vad some.wav` lists
the speech segments. `translate some.wav` streams a WAV file to OpenAI and prints the lines (set the
`OPENAI_API_KEY` environment variable first). Add `--engine soniox` (and optionally `--hints tr,no`) to use
Soniox instead, with `SONIOX_API_KEY`.

### Stage 2: settings, hotkeys, glossary, transcript history

1. **Settings tab:** change the font, size, background opacity, max lines and fade time. The overlay updates
   immediately. Settings are saved to `%APPDATA%\LiveSubtitles\settings.json`. The API key is never stored there.
2. **Hotkeys:** click a hotkey box and press a new combination. The defaults are:

   | Action | Hotkey |
   |---|---|
   | Start/stop | `Ctrl+Alt+S` |
   | Pause | `Ctrl+Alt+P` |
   | Show/hide overlay | `Ctrl+Alt+O` |
   | Click-through | `Ctrl+Alt+T` |

   Focus another app (a browser or game) and press them. The status line under the boxes says if a
   combination is already taken by another program.
3. **Tray:** close or minimise the window. The app keeps running in the notification area. Right-click the icon
   for start/stop, pause, overlay and exit.
4. **Glossary:** run a test file or video that mentions a name.
   - Add a **Keep as-is** rule with the correct spelling, plus any wrong spellings the model produced as aliases.
     New lines use the corrected spelling immediately, even mid-session.
   - A **Replace** rule changes words in the English text, e.g. make "brother" read "abi".
   - Rules are applied locally. The API has no glossary input.
5. **History tab:** every line with time, speaker, translation and original. **Export .txt** and
   **Export .srt** save the current session. Load the .srt next to a screen recording to check the timing.
   **Save transcripts automatically** is off by default. When on, each final line is appended to a text file in
   the chosen folder.

### Stage 3: automatic speaker labels, rename/merge, test dialogue generator

How it works: each speech segment gets a 512-number voice fingerprint from the local CAM++ model (ONNX Runtime
on your CPU). It is compared with each voice heard so far.

- A clear match gets that label, and the voice's profile improves with each line.
- A clearly new voice becomes "Speaker N" with a new colour.
- Anything in between shows **"?"**. The app never guesses. Several similar "?" lines together become a new
  speaker, and earlier "?" lines are relabelled retroactively.
- Voices that turn out to be the same person are merged automatically.

Only voices you rename are stored, as fingerprints (no audio), in `%APPDATA%\LiveSubtitles\voices.json`.

Why CAM++ instead of ECAPA-TDNN: no maintained ECAPA-TDNN ONNX export was available. CAM++ (3D-Speaker) is the
newer model from the same line of work. It is more accurate on VoxCeleb (EER 0.65 %), and smaller and faster
on a CPU.

Testing:

1. **Test tools tab → Generate test dialogue:** pick Turkish or Norwegian and 3 speakers, then click
   **Generate**. After about 20–40 s you get a .wav in `Music\LiveSubtitles test audio` and a script showing
   who said what.
2. Click **▶ Play through the pipeline**. Lines appear as `Speaker 1: …`, `Speaker 2: …` in different colours.
   A line shows "…" until its voice is checked. Compare with the script: the voices are fictional, so the
   numbering can differ, but each voice should keep the same label. On the **Debug** tab, the Confidence column
   shows the similarity to the best and next-best voice.
3. **Rename:** turn click-through off, then click a name on the overlay. Or click it on the History tab, or
   double-click it on the Speakers tab. Enter e.g. "Emre". All of that voice's past lines update. Stop, then
   play the same file again: "Emre" is recognised automatically. That voice is now listed as *remembered*.
4. **Fix mistakes:**
   - On the History tab, right-click a line → **This line was said by ▸** to move it (the profile learns from
     this).
   - **This speaker is the same person as ▸** merges two labels. You can also merge from the Speakers tab.
   - Click a "?" name to say who it was.
5. **Mute:** tick **Mute** for one speaker and replay. Once that voice is recognised (about 1.2 s into each
   line), that speaker's audio is not sent at all. The Debug tab shows "muted (not sent)", and no lines appear
   for them.
6. **Forget / Clear all voices** delete profiles. Their lines fall back to "?".
7. Your own voice test: play a YouTube video with several speakers (a podcast or panel) using **Any app**.

Headless check: `dotnet run --project tools/LiveSubtitles.Cli -- diarize some.wav` prints each segment's speaker
with similarity scores.

### Stage 4: cost tracking, reconnects, call-quality simulation, polish

1. **Cost:** the top bar shows `≈ $0.012 this session (0.3 min)`. It updates as speech is sent. Silence between
   speakers is not sent, so a quiet call costs very little.
2. **Spending cap:** in Settings → Cost, set e.g. `0.05`, then play a long file or video.
   - When the cap is reached, the session pauses by itself.
   - The overlay shows "Paused — spending cap reached", and a message explains how to continue.
   - Resume only works after you raise or clear the cap.
3. **Reconnect:** while a file or video is playing, disconnect from the network (turn off Wi-Fi or unplug the
   cable) for 10 seconds, then reconnect.
   - The status line and a small pill on the overlay show "Reconnecting…" with an increasing delay
     (1 s, 2 s, 4 s … up to 30 s).
   - Speech captured while offline (up to 20 s) is sent after reconnecting.
   - The history keeps every line. OpenAI sessions are also rotated automatically shortly before they expire,
     during a pause in speech.
   - An invalid key is not retried endlessly: the status says what is wrong.
4. **Call-quality simulation:** tick it on the Session tab (Audio file) or the Test tools tab. You can toggle it
   while a file plays. The effect:
   - narrows the voice band;
   - varies each line's volume, as with different people's microphones;
   - adds light compression and a quiet noise floor;
   - drops rare 20–40 ms packets.

   Generated dialogues then behave more like a real call, which is a harder test for speaker labels.
5. **Latency:** the top bar shows the text lag (how far subtitles trail the audio) and how long after someone
   stops talking their line becomes final. The Debug tab shows these values for each segment.
6. **Errors** go to `%APPDATA%\LiveSubtitles\logs\livesubtitles-YYYYMMDD.log` (Debug tab → Open log folder).
   Logs are kept for 14 days.

### Stage 5: installer

1. Install from the GitHub Actions artifact (or build it, see [Install](#install)). Tick "Start when I sign in" to
   test autostart.
2. Start it from the Start menu. The app starts, finds the bundled models (no "model missing" message on the
   Speakers tab), and the Debug tab's status says *Silero VAD*.
3. Repeat the stage 1 test with an audio file or YouTube, and the stage 3 test with a generated dialogue.
4. Uninstall and answer **Yes** to the question about deleting data: `%APPDATA%\LiveSubtitles` and the saved key
   are removed.

The GitHub workflow also runs `LiveSubtitles.exe --smoke-test` on a real Windows machine after each build. It:

- opens every window and tab;
- pushes sample lines through the overlay and history;
- fails on any XAML or data-binding error;
- decodes an audio file through Media Foundation;
- tries process-loopback activation.

The result is in the `smoke-test-output` artifact.

## Known limitations

- **Overlapping speech:** Discord mixes all voices into one stream, so when two people talk at once the line goes
  to whoever dominates. Short interjections ("yes", "haha") under about 1 s often show "?", because they are too
  short to identify a voice reliably.
- **Line boundaries:** the translation API streams text without utterance boundaries, about 1–1.5 s behind the
  speech. The app matches text to speech segments by timing, punctuation and the original transcript. On recorded
  API output this fixed every cross-speaker bleed in the Norwegian test (previously 5 of 20 lines). A word can
  still land on the neighbouring line when turns are very fast, and more so if the original-language text is
  turned off, since it anchors the matching.
- **Fast replies:** turns are split on pauses of 0.3 s. When two people still end up in one segment, the app
  notices the voice change and doesn't learn from that segment. Its line is labelled with whoever starts it, or
  "?".
- **Glossary:** the model has no glossary or prompt input, so the glossary can only fix the text afterwards. It
  cannot stop the model from mistranslating a slang word in the first place.
- **Same-language speech:** English speech is shown from the original transcript (so keep "Show the original"
  on), and the check is a simple word list. Mixed Turkish/English sentences count as Turkish and get translated.
  To save cost for people who always speak English, mute them.
- **Soniox:** the model id defaults to `stt-rt-v5` (Settings → Advanced tuning). If Soniox renames its models,
  the app reports "Soniox refused the settings" and you can change the id there. Soniox translates in chunks, so
  a translated line can appear slightly later than its original text.
- **Exclusive fullscreen games** hide every overlay. Use borderless windowed mode.
- **Speaker labels** depend on audio quality. Discord's noise suppression and very similar voices can split one
  person into two labels or merge two people. In testing, two similar female TTS voices ("coral" and "marin")
  were merged consistently. Use Merge and "This line was said by" to correct it; the profiles learn from these
  corrections. A voice gets its label from about its second short phrase, and earlier "?" lines are then
  corrected.
- **Connection drops:** OpenAI sometimes closes the connection (25 times in 16 minutes on one test day, including
  with the plain API). The app reconnects in about 1.5–2 s and re-sends the speech not yet translated. Words from
  around the drop can still be translated slightly differently.
- The cost shown is an estimate from audio minutes sent. The service's own billing is authoritative.
- **Testing so far:**
  - The API details were checked against OpenAI's documentation, SDK and a live test run
    (see `VPS-TEST-BRIEF.md`). That run found 6 bugs, all fixed.
  - The automated tests replay text recorded from the real API, and run diarization on real multi-speaker
    recordings and a fast-turn-taking reproduction.
  - What hasn't been tested yet: real Discord audio on Windows.

## Troubleshooting

| Symptom | What to check |
|---|---|
| "Waiting for Discord to start…" although Discord is open | Discord, Discord PTB and Canary are detected. For another client, use **Any app** and pick it. |
| Nothing happens when people talk | Watch **Speech detected** in the top bar. If it stays empty, Discord's output may be muted or deafened, or you picked the wrong app. Try the **Whole output device** source to compare. |
| "OpenAI rejected the API key (401)" | Create a new key and use **Test key**. |
| "Soniox rejected the API key" / "out of credit" | Check the key and credit at console.soniox.com, then **Test key**. |
| "Soniox refused the settings" | Usually an unknown model id or language code. Check Settings → Advanced → Soniox model id, and the language hints. |
| "…not available to this project (404/403)" | The key's project has no access to `gpt-realtime-translate`. Check the project's model permissions on platform.openai.com. |
| "Rate limited or out of credit (429)" | Add credit under Billing, or raise the project's rate limits. |
| Hotkey doesn't work | Another app has taken it (the Settings tab says which). Pick another combination. |
| Overlay not visible in a game | Switch the game to borderless windowed mode. |
| Anything else | Debug tab → **Open log folder** and look at today's log. It contains no audio, text or key, so it's safe to share. |

## Project layout

```
src/LiveSubtitles.Core      platform-independent pipeline: resampler, VAD, segmenter, OpenAI + Soniox clients,
                            text-to-segment matching, speaker model + clustering, glossary, export, cost
src/LiveSubtitles.App       Windows WPF app: WASAPI capture (NAudio), overlay, main window, tray, hotkeys,
                            Credential Manager
tests/LiveSubtitles.Core.Tests   unit + integration tests (real speech, fake OpenAI and Soniox servers)
tools/LiveSubtitles.Cli     headless vad / diarize / translate on a WAV file
installer/                  Inno Setup script and build script
```
