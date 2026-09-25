# OpenAI docs findings (checked 2026-09-26)

Sources (all fetched as Markdown by adding `.md` to the page URL):

- G = https://developers.openai.com/api/docs/guides/realtime-translation
- M = https://developers.openai.com/api/docs/models/gpt-realtime-translate
- W = https://developers.openai.com/api/docs/models/gpt-realtime-whisper
- CE = https://developers.openai.com/api/reference/resources/realtime/translation-client-events
- SE = https://developers.openai.com/api/reference/resources/realtime/translation-server-events
- P = https://developers.openai.com/api/docs/pricing
- C = https://developers.openai.com/api/docs/guides/realtime-costs (same text at /guides/voice-latency-cost)
- RC = https://developers.openai.com/api/docs/guides/realtime-conversations
- CB = https://developers.openai.com/cookbook/examples/voice_solutions/realtime_translation_guide (cookbook, not reference docs)

## 1. Endpoint and auth

**Yes.** URL and Bearer auth match. `OpenAI-Beta` is not used anywhere in the translation docs. Every example also sends
`OpenAI-Safety-Identifier`, but it is not described as required.

> `"wss://api.openai.com/v1/realtime/translations?model=gpt-realtime-translate",` … `Authorization: \`Bearer ${process.env.OPENAI_API_KEY}\`,` `"OpenAI-Safety-Identifier": "hashed-user-id",` (G, "Create a WebSocket session")

> `| Realtime translation | \`v1/realtime/translations\` | Supported |` (M)

Also documented: WebRTC via `POST /v1/realtime/translations/client_secrets` and `POST /v1/realtime/translations/calls` (G).

## 2. Events

The documented set is **exactly** the set the app handles. Nothing more is documented: no `*.done`, no rate-limit, segment or turn events.

- Client (CE): `session.update`, `session.input_audio_buffer.append`, `session.close`.
- Server (SE): `error`, `session.created`, `session.updated`, `session.closed`, `session.input_transcript.delta`,
  `session.output_transcript.delta`, `session.output_audio.delta`.

> "Translation starts from the audio stream itself. Keep appending audio, including silence between phrases, and handle output events as they arrive." (G)

> `error`: "Most errors are recoverable and the session will stay open, we recommend to implementors to monitor and log error messages by default." (SE)

`session.output_audio.delta` also carries `sample_rate`, `channels`, `format: "pcm16"`, `elapsed_ms` (SE).
Every event also has an `event_id` field.

## 3. Disabling output audio

**Not documented. There is no such setting.** `session.update` accepts only three fields:

> "Translation sessions support updates to `audio.output.language`, `audio.input.transcription`, and `audio.input.noise_reduction`." (CE)

> "Output modalities: audio, text" (M)

## 4. `elapsed_ms`

The docs are vague. They do not say whether it is input-audio time or emission time.

> "Timing metadata for stream alignment, derived from the translation frame when available. It advances in 200 ms increments, but multiple transcript deltas may share the same `elapsed_ms`. Treat it as alignment metadata, not a unique transcript-delta identifier." (SE, on both input and output transcript deltas)

"Derived from the translation frame" and "200 ms increments" (the input frame size) suggest that it counts input
audio frames consumed. The probe in step 5 tests this. The field is `optional number or null`, so it can be missing.

## 5. Session limits

- `expires_at`: "Expiration timestamp for the session, in seconds since epoch." (SE). No value or duration is given.
- For translation sessions specifically: **not documented**. For Realtime sessions in general, RC says: "The maximum duration of a Realtime session is **60 minutes**."
- What happens at expiry: **not documented**.
- Idle timeout with no audio: **not documented** for translation. Only the pause semantics are documented:

> "Keep appending silence while the session is active. If a client stops sending audio and later resumes, model time treats the resumed audio as contiguous with the previous audio rather than as a real-world pause." (CE)

## 6. Billing

Billed by **audio duration**, not tokens. The docs don't say whether "duration" means audio sent or wall-clock connected time.
C implies audio: "Streaming translation and streaming transcription sessions are billed by audio duration."

> "GPT-Realtime-Translate is priced by audio duration rather than text tokens." `| Price | $0.034 | minute |` (M)

> `| gpt-realtime-translate | Live translation | - | - | $0.034 / minute |` `| gpt-realtime-whisper | Live transcription | - | - | $0.017 / minute |` (P)

Whether `gpt-realtime-whisper` input transcription **inside a translation session** is billed extra: **not documented
for translation**. For voice-agent sessions, C says: "the Realtime API bills for input transcriptions, if enabled.
Input transcription uses a different model … and thus are billed from a different rate card." Assume it may be
+$0.017/min (total up to $0.051/min) until the usage dashboard shows otherwise.

## 7. Prompts and glossaries

**Not supported.**

> "This model does not currently support custom prompting or voice selection parameters." (CB)
> "The model does not currently support custom prompts, glossaries, or pronunciation guides." (CB)

The reference (CE) lists no prompt field.

## 8. `output.language`

It is a string. All examples use ISO-639-1 codes (`"es"`, `"fr"`). No enum is given in the reference:

> "`language: optional string` Target language for translated output audio and transcript deltas." (CE)

List (CB only, names not codes):

> "Realtime Translation currently supports 13 target output languages: Spanish, Portuguese, French, Japanese, Russian, Chinese, German, Korean, Hindi, Indonesian, Vietnamese, Italian, and English."

This matches `TranslationProtocol.OutputLanguages` (en es pt fr ja ru zh de ko hi id vi it).

## 9. Input languages

**Turkish: yes. Norwegian: yes, listed as "Norwegian" and "Nynorsk"** (Bokmål is not named separately):

> "It can dynamically detect and translate from … Nepali, Norwegian, Nynorsk, Polish, … Thai, Turkish, Ukrainian, …" (CB, "Supported languages")

This is documented only in the cookbook. The guide and model pages give no list.

## 10. Silence, gaps and chunk sizes

> "Translation consumes 200 ms engine frames. For best realtime behavior, append audio in 200 ms chunks. If a chunk is shorter, the server buffers it until it has enough audio for one frame. If a chunk is longer, the server splits it into 200 ms frames and enqueues them back-to-back." (CE)

> "Keep appending silence while the session is active. If a client stops sending audio and later resumes, model time treats the resumed audio as contiguous with the previous audio rather than as a real-world pause." (CE)

> Production checklist: "Stream audio continuously, including silence between phrases." (G)

> "Stream 24 kHz PCM16 audio with `session.input_audio_buffer.append`, including silence between phrases." (CB)

Unsupported formats: "Unsupported websocket audio formats return a validation error" (CE).

## 11. Noise reduction

`noise_reduction` is `{ "type": "near_field" | "far_field" }` or `null`:

> "Optional input noise reduction. Set to `null` to disable it." … "`near_field` is for close-talking microphones such as headphones, `far_field` is for far-field microphones such as laptop or conference room microphones." (CE)

Default: **not documented**. The `session.created` example shows `near_field`, but that is only an example. See the
probe's `session.created` for the real default.

## 12. Other points vs README "How it works"

- **Silence skipping goes against the documented guidance.** The docs say three times to stream continuously,
  including silence (CE, G, CB). The app's default sends only speech, pre-roll and a 1.5 s tail. The README describes
  the "contiguous" behaviour correctly and mitigates it with the tail. But this is an explicit deviation from the
  production checklist, so the quality impact must be measured (step 6).
- **Mixed speakers.** G and CB: "Keep participant audio tracks separate. Mixing speakers into one stream makes
  speaker identity, speaker captions, and overlapping speech more difficult to handle." The app sends one mixed
  Discord stream by design. This is a known trade-off, not a bug.
- **Same-language speech** (README, correct): "Realtime Translation tries not to translate speech that is already in
  the selected output language … the model may not produce translated audio for that segment." (CB)
- **`session.close` flush** (README doesn't cover it, app does it): "After you send `session.close`, stop appending
  audio and continue reading events … until you receive `session.closed`. Closing the socket immediately can drop
  translated output still draining from the session." (G). The app waits up to 5 s on rotation and 3 s on stop.
- **Deltas are append-only; don't insert spaces.** "Clients should not insert unconditional spaces between deltas." (SE)
- The input-transcription example includes `"language": "en"` inside `transcription`, but the reference schema lists
  only `model`. This is an undocumented optional field.
- README says "checked against the SDK type definitions and the cookbook". This is consistent with the current
  reference. No contradictions were found in endpoint, events, audio format, the output-language list or prompt support.
