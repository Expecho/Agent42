namespace Agent42.Tests;

/// <summary>
/// Guards the speech-input (voice dictation) wiring in the composer. Serves the real Backend
/// <c>wwwroot</c> through the shared <see cref="StaticAssetsServerFixture"/>, then asserts on the
/// presence of the key source strings. These are source-text heuristics (the repo has no JS test
/// runner by design — see <see cref="PwaAssetsTests.ServiceWorker_BypassesApiBeforeCaching"/>):
/// they prove the wiring exists, not that the Web Speech API behaves at runtime. The runtime half
/// (dictation, interim streaming, profile→lang binding, review-before-send) is verified manually
/// in DevHost with a real mic — the browser speech engine can't be driven headlessly.
/// </summary>
[Collection("StaticAssets")]
public sealed class SpeechInputAssetsTests
{
    private readonly HttpClient _client;

    public SpeechInputAssetsTests(StaticAssetsServerFixture fixture) => _client = fixture.Client;

    [Fact]
    public async Task MicButton_IsPresent()
    {
        var index = await _client.GetStringAsync("/");
        Assert.Contains("id=\"micBtn\"", index);
    }

    [Fact]
    public async Task AppJs_FeatureDetectsSpeechRecognition()
    {
        var app = await _client.GetStringAsync("/app.js");
        // Vendor-prefixed form is the one Chrome/Edge expose; its presence proves the
        // feature-detect guard exists (and thus the Firefox hide-the-button path).
        Assert.Contains("webkitSpeechRecognition", app);
    }

    [Fact]
    public async Task AppJs_EnablesInterimResults()
    {
        var app = await _client.GetStringAsync("/app.js");
        Assert.Contains("interimResults", app);
    }

    [Fact]
    public async Task AppJs_DrivesRecognitionLanguageFromDevice()
    {
        var app = await _client.GetStringAsync("/app.js");
        // navigator.language is the genuinely new token here (preferredLanguage already ships
        // with the profile feature, so it would prove nothing about speech wiring). The
        // ".lang =" assignment binds the language to the recognition instance; together they
        // prove the wiring, though not the runtime value — that's the manual half of AC4.
        Assert.Contains("navigator.language", app);
        Assert.Contains(".lang =", app);
    }
}
