# Live Subtitles for Discord

A Windows desktop app that shows live English subtitles for Discord voice calls. It uses OpenAI's
Realtime Translation model (`gpt-realtime-translate`) and runs entirely on your PC: no bots, no servers,
nothing for the people you talk to to install. The only external service is the OpenAI API.

- Captures **only Discord's audio**, using Windows per-app loopback capture. Your microphone, games and
  music are excluded.
- A local voice detector (Silero VAD) means only speech is sent to OpenAI, not silence.
- The overlay is transparent and always on top. It streams partial text, then replaces it with the final line.
- Shows the original-language transcript under each subtitle (optional).
- Test mode: use any app (e.g. a YouTube video in Chrome) or a local audio file as the source.
- Debug panel: speech segments, latency and raw text.

> Status: this README is updated after each build stage. See [Testing each stage](#testing-each-stage).

## Requirements

- Windows 10 version 2004 (build 19041) or newer, or Windows 11. Per-app capture needs 2004+. On older builds
  the app falls back to capturing a whole output device.
- An OpenAI API key with access to `gpt-realtime-translate` (see below).
- For games: run the game in **borderless windowed** or windowed mode. Exclusive fullscreen draws over every
  overlay.

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

## Building and running from source

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (x64). From this folder:

```powershell
dotnet build -c Release
dotnet run --project src/LiveSubtitles.App -c Release
```

The first build downloads the two local models into `models/` and checks their SHA-256 hashes:

| Model | Purpose | Size | Licence |
|---|---|---|---|
| Silero VAD v5 | Speech detection | 2.3 MB | MIT |
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
  **There are no "done" events** and no turns. The app decides when a line is final: when the model has moved
  past that speech segment, or when no more text arrives shortly after the speaker stops.
- Translated **audio cannot be switched off**. The app ignores `session.output_audio.delta`.
- The model **does not accept prompts or glossaries**, so the glossary is applied locally to the text (stage 2).
- The model does not translate speech that is already in the output language. See "Speech that is already in
  English" on the Session tab.
- When audio stops and later resumes, the model treats it as continuous. The app therefore sends a short
  real-silence tail after speech (default 1.5 s) and pads to a whole 200 ms frame, so the model finishes each
  sentence. **Send continuously** (Settings) sends everything, for the best quality at a higher cost.

## Privacy

- Audio is processed in memory and is **never saved to disk**. The exceptions are test files you choose or generate
  in test mode.
- Nothing is sent anywhere except the OpenAI API (speech audio only, over TLS).
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
`OPENAI_API_KEY` environment variable first).
