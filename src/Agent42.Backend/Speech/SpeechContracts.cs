

namespace Agent42.Backend;

// Client payload for POST /api/speech (ADR 0038): the text to synthesize into audio via the user's default
// speech model. One sentence per call in practice (the voice controller speaks sentence-by-sentence).
public record SpeechRequest(string Text);
