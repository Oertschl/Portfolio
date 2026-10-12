using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Attach this alongside the existing ToneBlock component (same GameObject).
/// It does NOT touch ToneBlock.PlaySound() or any existing trigger logic --
/// it purely listens to whatever ToneBlock's AudioSource is actually
/// outputting, which sidesteps the desync problem we diagnosed: this
/// reads the audio itself rather than reacting to the moment Play() was
/// called.
///
/// OnAudioFilterRead runs on Unity's AUDIO THREAD, not the main thread.
/// We must not touch GameObjects/Components from inside it. So it only
/// copies samples into a buffer; the main-thread Update() then runs the
/// actual extractor pipeline and feeds visualizers.
/// </summary>
[RequireComponent(typeof(AudioSource))]
public class NoteAudioVisualizer : MonoBehaviour
{
    [SerializeField]
    private int windowSize = 2048;

    [SerializeField]
    [Tooltip("Verbose logging to confirm capture/extractor pipeline is actually running. Turn off once verified -- this logs every frame while a note plays.")]
    private bool debugLogging = true;

    public enum ResponderMode
    {
        RMS_Envelope,       // Responder 1: amplitude → size + rate
        FFT_DominantFreq,   // Responder 2: dominant Hz → color
        Chroma_PitchClass,  // Responder 3: chroma → color + lifetime
        Onset_Flux,         // Responder 4: onset strength → burst
        STFT_Bands,         // Responder 5: spectral bands → size/rate/hue
        Combined            // Responder 6: ground-truth color + STFT density + onset burst,
                             // normalized per-note so it stays consistent across instruments/timbres
    }

    [SerializeField]
    [Tooltip("Which analysis method drives the visual. Change this in the Inspector " +
             "to switch between responders without recompiling. Keep the single-extractor " +
             "responders (RMS/FFT/Chroma/Onset/STFT) available for isolated evaluation/testing; " +
             "use Combined for actual gameplay/study visuals.")]
    internal ResponderMode responderMode = ResponderMode.RMS_Envelope;

    [SerializeField]
    [Tooltip("Assign the existing MusicWave_Sorting ParticleSystem here.")]
    private ParticleSystem targetParticles;

    [SerializeField]
    [Tooltip("Tuning knobs for ResponderMode.Combined -- see CombinedResponder.cs for what each " +
             "field does. Safe to tweak between Play sessions (Edit mode) to iterate on feel.")]
    private CombinedResponder.Settings combinedSettings = new CombinedResponder.Settings();

    private readonly List<IAudioFeatureExtractor> extractors = new List<IAudioFeatureExtractor>();
    private readonly List<IVisualResponder> responders = new List<IVisualResponder>();

    // Double-buffer: audio thread writes into `captureBuffer`,
    // main thread reads the last-completed copy from `readyBuffer`.
    // A lock is cheap here since this only guards a buffer swap, not the
    // whole audio callback.
    private readonly object bufferLock = new object();
    private float[] captureBuffer;
    private float[] readyBuffer;
    private double readyDspTime;
    private bool hasNewData;

    private AudioFeatureFrame currentFrame;

    private AudioSource toneSource;
    private ToneBlock toneBlock;
    private EvaluationLogger evaluationLogger;
    private AudioDebugVisualizer debugVisualizer;

    /// <summary>Called by EvaluationLogger.Start() to register itself.</summary>
    public void SetEvaluationLogger(EvaluationLogger logger)
    {
        evaluationLogger = logger;
        Debug.Log($"[NoteAudioVisualizer] EvaluationLogger registered on '{cachedName}'");
    }

    // Cached on the main thread in Awake -- gameObject.name itself is a
    // property getter on a UnityEngine.Object and is NOT safe to call
    // from OnAudioFilterRead (the audio thread), even though Debug.Log
    // itself is thread-safe. Same rule as Transform/GameObject access in
    // general -- it just isn't obvious until you hit it, like we just did.
    private string cachedName;

    private int cachedSampleRate;

    // Shared across every NoteAudioVisualizer instance -- the same handful of AudioClips (one per
    // note/instrument combination) get replayed constantly across many ToneBlocks, so computing each
    // clip's peak/tail-start once and reusing it avoids repeating the same AudioClip.GetData() scan
    // every note.
    private struct ClipAnalysis
    {
        public float peak;
        public float tailStartSeconds;
    }
    private static readonly Dictionary<AudioClip, ClipAnalysis> clipAnalysisCache = new Dictionary<AudioClip, ClipAnalysis>();

    // How quiet (as a fraction of the clip's own peak) counts as "no real content here anymore" when
    // scanning for the tail-start point. Deliberately small -- this only needs to find the point past
    // which the signal never comes back up, not the point where it merely gets quiet mid-decay.
    private const float TAIL_DETECT_FRACTION = 0.05f;

    /// <summary>
    /// Peak amplitude and tail-start timestamp for a clip, computed once per distinct AudioClip via a
    /// single AudioClip.GetData() pass and cached. Peak is used by CombinedResponder to normalize this
    /// note's envelope against its own true dynamic range instead of an absolute amplitude (see
    /// AudioFeatureFrame.clipPeakAmplitude). tailStartSeconds marks the point after which the clip
    /// never rises back above TAIL_DETECT_FRACTION of its own peak -- i.e. where real musical content
    /// ends and only a quiet decay/reverb/silence tail remains (see AudioFeatureFrame.clipTailStartSeconds).
    /// </summary>
    private static ClipAnalysis GetOrComputeClipAnalysis(AudioClip clip)
    {
        if (clip == null) return new ClipAnalysis();
        if (clipAnalysisCache.TryGetValue(clip, out ClipAnalysis cached)) return cached;

        // GetData() can throw for some clip load configurations (e.g. certain streaming setups) --
        // never let that take down the whole visualizer pipeline. On failure, cache zeros so
        // responders fall back to un-normalized/always-live behavior for this clip rather than
        // retrying and potentially logging every single frame.
        ClipAnalysis result = new ClipAnalysis();
        try
        {
            int channels = clip.channels;
            int sampleCount = clip.samples;
            float[] samples = new float[sampleCount * channels];
            clip.GetData(samples, 0);

            float peak = 0f;
            for (int i = 0; i < samples.Length; i++)
            {
                float a = Mathf.Abs(samples[i]);
                if (a > peak) peak = a;
            }

            // Scan backward (per-frame across channels) for the last frame that still exceeds the
            // tail-detect fraction of peak. Everything after that is the "dead" tail.
            float tailThreshold = peak * TAIL_DETECT_FRACTION;
            int lastLiveFrame = sampleCount - 1;
            if (tailThreshold > 0f)
            {
                lastLiveFrame = -1;
                for (int frame = sampleCount - 1; frame >= 0; frame--)
                {
                    bool loudEnough = false;
                    for (int c = 0; c < channels; c++)
                    {
                        if (Mathf.Abs(samples[frame * channels + c]) > tailThreshold) { loudEnough = true; break; }
                    }
                    if (loudEnough) { lastLiveFrame = frame; break; }
                }
            }

            result.peak = peak;
            result.tailStartSeconds = (lastLiveFrame >= 0 && clip.frequency > 0)
                ? (float)lastLiveFrame / clip.frequency
                : 0f; // no clear tail found (or silent clip) -- 0 disables the ground-truth gate, live threshold still applies
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[NoteAudioVisualizer] Could not analyze clip '{clip.name}' ({e.Message}) " +
                              "-- CombinedResponder will fall back to un-normalized/live-threshold-only " +
                              "behavior for this clip.");
            result = new ClipAnalysis();
        }

        clipAnalysisCache[clip] = result;
        return result;
    }

    /// <summary>
    /// Ground-truth instrument for the note currently on this block, fetched the same way
    /// ToneBlock.Start() does (PhraseSequenceManager.GetCurrentPhrase().instrument). Defensive
    /// null-checks throughout since this runs from Update(), potentially before everything else has
    /// initialized, or outside of an active phrase sequence (e.g. a stray ToneBlock in a test scene).
    /// </summary>
    private int GetCurrentInstrumentIndex()
    {
        if (ToneGameManager.instance == null) return 0;
        PhraseSequenceManager sequenceManager = ToneGameManager.instance.GetPhraseSequenceManager();
        MusicPhrase currentPhrase = sequenceManager != null ? sequenceManager.GetCurrentPhrase() : null;
        return currentPhrase != null ? (int)currentPhrase.instrument : 0;
    }

    private void Awake()
    {
        captureBuffer = new float[windowSize];
        readyBuffer = new float[windowSize];
        toneSource = GetComponent<AudioSource>();
        toneBlock = GetComponent<ToneBlock>();
        debugVisualizer = GetComponent<AudioDebugVisualizer>();
        cachedName = gameObject.name;
        cachedSampleRate = AudioSettings.outputSampleRate;

        if (debugLogging)
        {
            string clipName = toneSource.clip != null ? toneSource.clip.name : "(null -- not assigned yet, ToneBlock sets this in its own Start())";
            Debug.Log($"[NoteAudioVisualizer] Awake on '{cachedName}'. " +
                      $"Found AudioSource (instance ID {toneSource.GetInstanceID()}), clip = {clipName}.");
        }

        // All five extractors registered -- each self-contained,
        // each reads AudioBuffer independently, each writes its own
        // fields in AudioFeatureFrame. Order doesn't matter.
        extractors.Add(new RmsEnvelopeFollower());
        extractors.Add(new FFTExtractor(windowSize));
        extractors.Add(new ChromaExtractor(windowSize));
        extractors.Add(new STFTExtractor(windowSize / 2, windowSize / 4, numFrames: 8));
        extractors.Add(new OnsetDetector(windowSize / 2, windowSize / 4, threshold: 0.03f));

        // Register exactly one responder based on the selected ResponderMode.
        // Each case wraps a different extractor's output for the same
        // targetParticles system, so switching modes in the Inspector swaps
        // which audio feature drives the visuals without touching this code.
        if (targetParticles != null)
        {
            IVisualResponder responder = responderMode switch
            {
                ResponderMode.RMS_Envelope     => new RmsResponder(targetParticles),
                ResponderMode.FFT_DominantFreq => new FFTResponder(targetParticles),
                ResponderMode.Chroma_PitchClass => new ChromaResponder(targetParticles),
                ResponderMode.Onset_Flux        => new OnsetResponder(targetParticles),
                ResponderMode.STFT_Bands        => new STFTResponder(targetParticles),
                ResponderMode.Combined          => new CombinedResponder(targetParticles, combinedSettings),
                _                               => new RmsResponder(targetParticles)
            };
            responders.Add(responder);
            Debug.Log($"[NoteAudioVisualizer] Responder registered: {responder.MethodName}, " +
                    $"PS={targetParticles.name}, " +
                    $"material={targetParticles.GetComponent<ParticleSystemRenderer>()?.sharedMaterial?.name ?? "NULL"}");
            if (debugLogging)
                Debug.Log($"[NoteAudioVisualizer] '{cachedName}' using responder: {responder.MethodName}");
        }
        else if (debugLogging)
        {
            Debug.LogWarning($"[NoteAudioVisualizer] '{cachedName}' has no targetParticles assigned.");
        }
    }

    // Throttle audio-thread logging so we don't flood the console --
    // OnAudioFilterRead fires many times per second (roughly every
    // ~10-20ms depending on DSP buffer size).

    // Track lock timing for performance evaluation -- this is a very
    // small critical section, but we want to confirm it doesn't ever
    // exceed a few microseconds. If it does, we can consider a lock-free ring buffer or other more complex approach, but for now this is simple and safe.
    private long maxLockMicroseconds = 0;
    private long totalLockMicroseconds = 0;
    private int lockMeasureCount = 0;
    private int audioCallbackCounter;
    private int updateCallCounter;
    private const int UPDATE_LOG_EVERY_N_FRAMES = 30;
    private const int AUDIO_LOG_EVERY_N_CALLBACKS = 50;

    // Called on the AUDIO THREAD by Unity. `data` is interleaved if
    // channels > 1; we sum to mono for simplicity (fine for a single
    // monophonic note sample -- revisit if clips are stereo and you care
    // about channel separation).




    private void OnAudioFilterRead(float[] data, int channels)
    {
        int frameCount = data.Length / channels;
        int copyCount = Mathf.Min(frameCount, captureBuffer.Length);

        float peakAbs = 0f;
        for (int i = 0; i < copyCount; i++)
        {
            float sum = 0f;
            for (int c = 0; c < channels; c++)
                sum += data[i * channels + c];
            captureBuffer[i] = sum / channels;
            float abs = Mathf.Abs(captureBuffer[i]);
            if (abs > peakAbs) peakAbs = abs;
        }

        if (debugLogging)
        {
            audioCallbackCounter++;
            if (audioCallbackCounter % AUDIO_LOG_EVERY_N_CALLBACKS == 0)
            {
                // I take Debug.Log as an exception since it never got officially confirmed as thread-safe, but in practice it seems to work fine on the audio thread.
                // The main risk is that if the log message itself accesses UnityEngine.Object properties (like gameObject.name), it could crash.
                // So we cache the name in Awake() and use that here.
                Debug.Log($"[NoteAudioVisualizer] OnAudioFilterRead firing on '{cachedName}' " +
                          $"(callback #{audioCallbackCounter}), channels={channels}, " +
                          $"frameCount={frameCount}, peakAbs={peakAbs:F4}, dspTime={AudioSettings.dspTime:F3}");
            }
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        lock (bufferLock)
        {
            System.Array.Copy(captureBuffer, readyBuffer, copyCount);
            readyDspTime = AudioSettings.dspTime;
            hasNewData = true;
        }
        sw.Stop();

        long microseconds = sw.ElapsedTicks /
                    (System.Diagnostics.Stopwatch.Frequency / 1_000_000L);
        if (microseconds > maxLockMicroseconds)
            maxLockMicroseconds = microseconds;
            totalLockMicroseconds += microseconds;
            lockMeasureCount++;

        if (lockMeasureCount % 200 == 0)
        {
            long avgUs = totalLockMicroseconds / lockMeasureCount;
            Debug.Log($"[LockTiming] Lock duration: avg={avgUs}µs, " +
                        $"max={maxLockMicroseconds}µs, " +
                        $"DSP buffer={copyCount * 1_000_000 / cachedSampleRate}µs total");
        }
    }

    private bool wasPlayingLastFrame;
    private int lastTimeSamples;
    private const int RESTART_THRESHOLD = 512; // if timeSamples jumps backward by more than this, a retrigger happened

    private void Update()
    {
        bool isPlayingNow = toneSource != null && toneSource.isPlaying;
        int currentTimeSamples = toneSource != null ? toneSource.timeSamples : 0;

        // Detect note start via two conditions, not just isPlaying flipping
        // False->True: an isPlaying-only check missed retriggers (the same
        // AudioSource replayed while already playing, e.g. rapid note taps),
        // since isPlaying never goes False in that case, leaving
        // noteJustStarted permanently False on repeated taps.
        // 1. Clean start: isPlaying flipped from False to True this frame.
        // 2. Retrigger: same AudioSource replayed while already playing --
        //    timeSamples resets to 0 on Play(), so if lastTimeSamples was
        //    advancing forward and currentTimeSamples is now less than
        //    lastTimeSamples, a retrigger happened.
        //    We use lastTimeSamples > RESTART_THRESHOLD (not just > 0)
        //    to avoid false positives on the first frame where both are 0.
        bool retriggered = isPlayingNow && wasPlayingLastFrame &&
                           lastTimeSamples > RESTART_THRESHOLD &&
                           currentTimeSamples < lastTimeSamples;
        bool justStarted = (isPlayingNow && !wasPlayingLastFrame) || retriggered;
        bool justStopped = !isPlayingNow && wasPlayingLastFrame;

        if (justStarted) updateCallCounter = 0;

        if (debugLogging && isPlayingNow != wasPlayingLastFrame)
        {
            Debug.Log($"[NoteAudioVisualizer] '{cachedName}' isPlaying changed: " +
                      $"{wasPlayingLastFrame} -> {isPlayingNow} at dspTime={AudioSettings.dspTime:F3}, " +
                      $"clip={(toneSource != null && toneSource.clip != null ? toneSource.clip.name : "null")}");
        }
        wasPlayingLastFrame = isPlayingNow;
        lastTimeSamples = currentTimeSamples;

        // Skip entirely when the note isn't playing -- OnAudioFilterRead
        // fires continuously (even on silence) for any enabled
        // AudioSource, so without this guard we'd run the full extractor
        // pipeline on every idle ToneBlock in the scene, every frame.
        if (!isPlayingNow)
        {
            // Only reset on the actual stop transition (this frame), not
            // every idle frame -- calling Reset() repeatedly while idle
            // would be harmless here but is wasted work at scale (many
            // idle notes sitting in the scene).
            if (justStopped)
            {
                foreach (var responder in responders)
                    responder.Reset();

                // Notify logger that the note ended -- must happen here
                // since we return early below and never reach the normal
                // RecordFrame call at the bottom of Update().
                if (evaluationLogger != null)
                {
                    string clipName = toneSource.clip != null ? toneSource.clip.name : "unknown";
                    float clipLength = toneSource.clip != null ? toneSource.clip.length : 0f;
                    evaluationLogger.RecordFrame(
                        in currentFrame,
                        noteJustStarted: false,
                        noteStopped: true,
                        currentClip: clipName,
                        currentPitchClass: toneBlock != null ? MusicPhrase.NoteToAbsoluteStep(toneBlock.blockNote) : 0,
                        expectedDuration: clipLength);
                }
            }

            lock (bufferLock)
            {
                hasNewData = false; // drop any stale buffer from just-finished playback
            }
            return;
        }
        // If no new audio buffer arrived this frame but a note just started,
        // fire responders immediately so Play() isn't delayed by DSP buffer timing.
        float[] samplesForThisFrame = null;
        double dspTimeForThisFrame = 0;

        lock (bufferLock)
        {
            if (!hasNewData)
            {
                if (justStarted)
                {
                    currentFrame = new AudioFeatureFrame();
                    currentFrame.noteJustStarted = true;
                    currentFrame.symbolicPitchClass = toneBlock != null ? MusicPhrase.NoteToAbsoluteStep(toneBlock.blockNote) : 0;
                    ClipAnalysis earlyAnalysis = GetOrComputeClipAnalysis(toneSource != null ? toneSource.clip : null);
                    currentFrame.clipPeakAmplitude = earlyAnalysis.peak;
                    currentFrame.clipTailStartSeconds = earlyAnalysis.tailStartSeconds;
                    currentFrame.clipPlaybackPositionSeconds = 0f; // note just started -- position 0
                    currentFrame.instrumentIndex = GetCurrentInstrumentIndex();
                    foreach (var responder in responders)
                        responder.Apply(in currentFrame);

                    // Notify logger on this early-fire frame so isRecording=true
                    // before any audio data arrives. Without this, noteStopped
                    // fires later with isRecording=false and WriteRow is skipped.
                    if (evaluationLogger != null)
                    {
                        string earlyClipName = toneSource.clip != null ? toneSource.clip.name : "unknown";
                        float earlyClipLength = toneSource.clip != null ? toneSource.clip.length : 0f;
                        evaluationLogger.RecordFrame(
                            in currentFrame,
                            noteJustStarted: true,
                            noteStopped: false,
                            currentClip: earlyClipName,
                            currentPitchClass: currentFrame.symbolicPitchClass,
                            expectedDuration: earlyClipLength);
                    }
                }
                return;
            }

            samplesForThisFrame = (float[])readyBuffer.Clone();
            dspTimeForThisFrame = readyDspTime;
            hasNewData = false;
        }

        AudioBuffer buffer = new AudioBuffer
        {
            samples = samplesForThisFrame,
            sampleRate = AudioSettings.outputSampleRate,
            dspTime = dspTimeForThisFrame,
            sourceId = cachedName
        };

        // Reset only the fields we expect this frame's extractors to fill;
        // a real implementation might pool/reuse AudioFeatureFrame
        // instances to avoid per-frame allocation.
        currentFrame = new AudioFeatureFrame();
        currentFrame.noteJustStarted = justStarted;
        currentFrame.symbolicPitchClass = toneBlock != null ? MusicPhrase.NoteToAbsoluteStep(toneBlock.blockNote) : 0;
        currentFrame.sampleRate = buffer.sampleRate;
        ClipAnalysis analysis = GetOrComputeClipAnalysis(toneSource != null ? toneSource.clip : null);
        currentFrame.clipPeakAmplitude = analysis.peak;
        currentFrame.clipTailStartSeconds = analysis.tailStartSeconds;
        currentFrame.clipPlaybackPositionSeconds =
            (toneSource != null && toneSource.clip != null && toneSource.clip.frequency > 0)
                ? (float)toneSource.timeSamples / toneSource.clip.frequency
                : 0f;
        currentFrame.instrumentIndex = GetCurrentInstrumentIndex();

        foreach (var extractor in extractors)
        {
            if (buffer.samples.Length >= extractor.RequiredSampleCount)
                extractor.Process(in buffer, ref currentFrame);
        }

        updateCallCounter++;
        if (debugLogging && (currentFrame.noteJustStarted || updateCallCounter % UPDATE_LOG_EVERY_N_FRAMES == 0))
        {
            Debug.Log($"[NoteAudioVisualizer] '{cachedName}' Update pipeline ran " +
                      $"(frame #{updateCallCounter}), bufferDspTime={buffer.dspTime:F3}, " +
                      $"rms={currentFrame.rmsValue:F4}, envelope={currentFrame.envelopeValue:F4}, " +
                      $"dominantHz={currentFrame.dominantFrequencyHz:F1}, " +
                      $"centroid={currentFrame.spectralCentroid:F1}, " +
                      $"chromaPitch={currentFrame.estimatedPitchClass} ({PitchClassName(currentFrame.estimatedPitchClass)}), " +
                      $"onset={currentFrame.onsetDetected} (str={currentFrame.onsetStrength:F4}), " +
                      $"noteJustStarted={currentFrame.noteJustStarted}, " +
                      $"stftReady={(currentFrame.stftMagnitudeFrames != null)}, " +
                      $"spectrumWritten={(currentFrame.spectrumMagnitudes != null)}");
        }

        // Feed currentFrame to every registered responder. Each responder
        // only reads from the frame, so adding a second/third responder
        // later (e.g. a mesh-deform responder alongside this particle one)
        // is just another line in the Awake() registration list above --
        // this loop doesn't change.
        foreach (var responder in responders)
        {
            responder.Apply(in currentFrame);
        }

        // Per-frame data accumulation for evaluation logger
        if (evaluationLogger != null)
            evaluationLogger.RecordPlayingFrame(in currentFrame, updateCallCounter);

        // Push fresh frame to debug visualizer after all extractors and responders have run
        if (debugVisualizer != null)
        {
            debugVisualizer.UpdateFrame(in currentFrame, responderMode);
        }
        // Start/stop transition recording
        if (evaluationLogger != null)
        {
            string clipName = toneSource.clip != null ? toneSource.clip.name : "unknown";
            float clipLength = toneSource.clip != null ? toneSource.clip.length : 0f;
            evaluationLogger.RecordFrame(
                in currentFrame,
                noteJustStarted: justStarted,
                noteStopped: justStopped,
                currentClip: clipName,
                currentPitchClass: currentFrame.symbolicPitchClass,
                expectedDuration: clipLength);
        }
    }

    /// <summary>Maps pitch class 0-11 to note name for readable logging.</summary>
    private static string PitchClassName(int pc)
    {
        string[] names = { "C","C#","D","D#","E","F","F#","G","G#","A","A#","B" };
        return (pc >= 0 && pc < 12) ? names[pc] : "?";
    }
}