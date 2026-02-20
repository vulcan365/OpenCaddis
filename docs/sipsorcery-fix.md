# SIPSorcery AudioExtrasSource — Queue-Based Audio Injection

## Problem

SIPSorcery's built-in `SendAudioFromStream` method creates a **second** `System.Threading.Timer` to pace RTP packets from a stream. The `AudioExtrasSource` already has its own timer that fires every 20ms to send silence frames. Running two timers on the .NET thread pool causes **dual-timer jitter** — the timers drift in and out of phase, producing choppy, stuttering audio on the receiving end.

Additionally, `AudioExtrasSource` must stay in `Silence` mode for the duration of a call. Calling `SetSource(None)` or `CloseAudio()` stops outbound RTP, which causes the remote PBX to stop sending inbound audio (RTP timeout). This rules out toggling the source mode to play audio and back.

## Solution

We added queue-based audio injection directly into the existing silence timer path. Instead of a second timer, PCM frames are enqueued into a `ConcurrentQueue<short[]>` and dequeued by the silence timer callback. One timer, no jitter.

### File Changed

`src/app/Media/Sources/AudioExtrasSource.cs` (SIPSorcery 8.0.11 source)

### Fields Added

```csharp
// Queue-based audio injection: feeds PCM frames through the existing source timer
// instead of using a separate stream timer. Avoids dual-timer jitter issues.
private readonly ConcurrentQueue<short[]> _audioQueue = new();
private TaskCompletionSource<bool> _audioQueueTcs;
```

### Methods Added

#### `QueueAudio(short[] pcmFrame)`

Enqueues a single frame of PCM audio (e.g. 160 samples for 20ms at 8kHz). Frames are sent FIFO by the silence timer.

```csharp
public void QueueAudio(short[] pcmFrame)
{
    _audioQueue.Enqueue(pcmFrame);
}
```

#### `QueueAllAudio(byte[] pcmData, int sampleRate)`

Slices a raw PCM byte buffer (16-bit LE) into timer-period-sized frames, enqueues them all, and returns a `Task` that completes when the last frame has been sent. This is the primary API for playing TTS output.

```csharp
public Task QueueAllAudio(byte[] pcmData, int sampleRate)
{
    if (pcmData == null || pcmData.Length == 0)
        return Task.CompletedTask;

    int samplesPerFrame = sampleRate / 1000 * _audioSamplePeriodMilliseconds;
    int totalSamples = pcmData.Length / 2;

    for (int i = 0; i < totalSamples; i += samplesPerFrame)
    {
        int count = Math.Min(samplesPerFrame, totalSamples - i);
        var frame = new short[samplesPerFrame];
        for (int j = 0; j < count; j++)
        {
            int byteIdx = (i + j) * 2;
            frame[j] = (short)(pcmData[byteIdx] | (pcmData[byteIdx + 1] << 8));
        }
        _audioQueue.Enqueue(frame);
    }

    var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    _audioQueueTcs = tcs;
    return tcs.Task;
}
```

#### `ClearQueuedAudio()`

Flushes all pending frames. Used for barge-in (caller interrupts TTS) and hangup cleanup.

```csharp
public void ClearQueuedAudio()
{
    while (_audioQueue.TryDequeue(out _)) { }
    _audioQueueTcs?.TrySetResult(true);
    _audioQueueTcs = null;
}
```

### Modified: `SendSilenceSample` Timer Callback

The existing silence timer callback was modified to check the queue before generating a silence frame:

```csharp
private void SendSilenceSample(object state)
{
    try
    {
        if (!_isClosed && !_streamSendInProgress && _sendSampleTimer != null)
        {
            lock (_sendSampleTimer)
            {
                short[] pcm;
                if (_audioQueue.TryDequeue(out var queuedPcm))
                {
                    pcm = queuedPcm;

                    // Signal completion when queue drains
                    if (_audioQueue.IsEmpty && _audioQueueTcs != null)
                    {
                        _audioQueueTcs.TrySetResult(true);
                        _audioQueueTcs = null;
                    }
                }
                else
                {
                    pcm = new short[_audioFormatManager.SelectedFormat.ClockRate
                           / 1000 * _audioSamplePeriodMilliseconds];
                }

                EncodeAndSend(pcm, _audioFormatManager.SelectedFormat.ClockRate);
            }
        }
    }
    catch (Exception e)
    {
        Log.LogError(e, "Exception sending silence sample");
    }
}
```

When the queue has frames, they are sent instead of silence. When the queue drains, it falls back to silence automatically — no mode switching, no RTP gaps.

## Usage

```csharp
// AudioExtrasSource must be in Silence mode for the call duration
mediaSession.AudioExtrasSource.SetSource(AudioSourcesEnum.Silence);

// Play TTS audio (fire-and-forget or awaitable)
byte[] pcmAudio = await speechPipeline.SynthesizeSpeechAsync("Hello!");
await mediaSession.AudioExtrasSource.QueueAllAudio(pcmAudio, 8000);

// Cancel playback (e.g. barge-in)
mediaSession.AudioExtrasSource.ClearQueuedAudio();
```

## Key Constraints

- **Do NOT use `SendAudioFromStream`** — it creates the dual-timer problem described above.
- **Do NOT call `SetSource(None)` or `CloseAudio()` during a call** — stopping RTP output causes the PBX to stop sending inbound audio.
- TTS audio format should be `Raw8Khz16BitMonoPcm` to match the G.711 codec rate (no resampling needed).
