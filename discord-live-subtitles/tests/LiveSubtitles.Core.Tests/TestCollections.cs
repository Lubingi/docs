namespace LiveSubtitles.Core.Tests;

/// <summary>CPU-heavy tests that run the ONNX speaker model: kept sequential so they don't starve the thread pool
/// that the async WebSocket/connection tests rely on.</summary>
[CollectionDefinition("SpeakerModel")]
public sealed class SpeakerModelCollection;
