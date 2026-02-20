using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;

namespace OpenCaddis.Services;

/// <summary>
/// Per-call wrapper around Azure Speech SDK continuous recognition.
/// Accepts raw PCM audio via PushAudio() and fires events on transcription results.
/// </summary>
public sealed class AzureSpeechPipeline : IAsyncDisposable
{
    private readonly PushAudioInputStream _audioStream;
    private readonly AudioConfig _audioConfig;
    private readonly SpeechRecognizer _recognizer;
    private readonly SpeechSynthesizer _synthesizer;
    private readonly string _callId;
    private readonly ILogger _logger;

    public event Action<string>? OnTranscriptionResult;
    public event Action<string>? OnError;

    public AzureSpeechPipeline(string subscriptionKey, string region, string callId, ILogger logger, string voiceName = "en-US-JennyNeural")
    {
        _callId = callId;
        _logger = logger;

        // G.711 decoded output: 8kHz, 16-bit, mono PCM
        var format = AudioStreamFormat.GetWaveFormatPCM(8000, 16, 1);
        _audioStream = AudioInputStream.CreatePushStream(format);
        _audioConfig = AudioConfig.FromStreamInput(_audioStream);

        var speechConfig = SpeechConfig.FromSubscription(subscriptionKey, region);
        _recognizer = new SpeechRecognizer(speechConfig, _audioConfig);

        // TTS: separate config for synthesis — raw 8kHz 16-bit mono PCM (no WAV header)
        // Matches the G.711 codec rate directly — no resampling needed
        var synthConfig = SpeechConfig.FromSubscription(subscriptionKey, region);
        synthConfig.SetSpeechSynthesisOutputFormat(SpeechSynthesisOutputFormat.Raw8Khz16BitMonoPcm);
        synthConfig.SpeechSynthesisVoiceName = voiceName;
        _synthesizer = new SpeechSynthesizer(synthConfig, null as AudioConfig);

        _recognizer.Recognized += (s, e) =>
        {
            if (e.Result.Reason == ResultReason.RecognizedSpeech && !string.IsNullOrWhiteSpace(e.Result.Text))
            {
                _logger.LogInformation("STT final result for call {CallId}: {Text}", _callId, e.Result.Text);
                OnTranscriptionResult?.Invoke(e.Result.Text);
            }
        };

        _recognizer.Canceled += (s, e) =>
        {
            if (e.Reason == CancellationReason.Error)
            {
                _logger.LogError("STT error for call {CallId}: {ErrorCode} - {ErrorDetails}",
                    _callId, e.ErrorCode, e.ErrorDetails);
                OnError?.Invoke($"{e.ErrorCode}: {e.ErrorDetails}");
            }
        };
    }

    public async Task StartAsync()
    {
        _logger.LogInformation("Starting STT pipeline for call {CallId}", _callId);
        await _recognizer.StartContinuousRecognitionAsync();
    }

    public void PushAudio(byte[] pcmData, int length)
    {
        _audioStream.Write(pcmData, length);
    }

    public async Task<byte[]?> SynthesizeSpeechAsync(string text)
    {
        var result = await _synthesizer.SpeakTextAsync(text);
        if (result.Reason == ResultReason.SynthesizingAudioCompleted)
        {
            var durationSec = result.AudioData.Length / (8000.0 * 2);
            _logger.LogInformation("TTS synthesized {Length} bytes ({Duration:F1}s) for call {CallId}", result.AudioData.Length, durationSec, _callId);
            return result.AudioData;
        }

        var cancellation = SpeechSynthesisCancellationDetails.FromResult(result);
        _logger.LogError("TTS failed for call {CallId}: {Reason} - {ErrorDetails}",
            _callId, cancellation.Reason, cancellation.ErrorDetails);
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _recognizer.StopContinuousRecognitionAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error stopping STT recognizer for call {CallId}", _callId);
        }

        _synthesizer.Dispose();
        _recognizer.Dispose();
        _audioConfig.Dispose();
        _audioStream.Dispose();

        _logger.LogInformation("STT pipeline disposed for call {CallId}", _callId);
    }
}
