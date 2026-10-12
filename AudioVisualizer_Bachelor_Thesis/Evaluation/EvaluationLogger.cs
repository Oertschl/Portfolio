using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// Attach to every ToneBlock alongside NoteAudioVisualizer.
///
/// Two recording modes:
///
/// NORMAL MODE (captureTimeSeries=false, default):
///   Logs one summary row per note tap to the shared EvaluationSession CSV.
///   Records peak values and sampled frames for thesis Claims 1-3.
///
/// TIME-SERIES MODE (captureTimeSeries=true):
///   On the NEXT tap only, logs every single frame to a separate
///   timeseries_<note>_<timestamp>.csv file, then automatically reverts
///   to normal mode. Use this to capture per-frame data for thesis figures.
///   Each row: frameNumber, dspTime, rms, envelope, dominantHz,
///              onsetStrength, onsetDetected, chromaPc, chroma[12],
///              plus one stft_binN_freqhz column per STFT bin.
/// </summary>
public class EvaluationLogger : MonoBehaviour
{
    [Tooltip("Which frames after onset to sample in normal mode")]
    [SerializeField] private int[] sampleFrames = { 1, 3, 10, 20, 30 };

    [Tooltip("When enabled: capture EVERY frame of the next tap to a separate " +
             "timeseries CSV, then auto-disable. Use for STFT/onset figures.")]
    [SerializeField] private bool captureTimeSeries = false;

    [Tooltip("Envelope value above which a frame counts as part of the note's " +
             "analysis-detected duration, independent of AudioSource.isPlaying. " +
             "Tune against peakEnvelope values in a normal-mode CSV.")]
    [SerializeField] private float envelopeThreshold = 0.01f;

    private void Reset()
    {
        sampleFrames = new int[] { 1, 3, 10, 20, 30 };
    }

    // ── Normal mode state ──────────────────────────────────────────────
    private bool isRecording = false;
    private int frameCount = 0;
    private double onsetDspTime;
    private string clipName;
    private int pitchClass;
    private float expectedDuration;
    private float peakRms;
    private float peakEnvelope;
    private float peakDominantHz;
    // -- Added for the Combined-responder timbre-normalization validation (thesis 5.3 addendum):
    // clipPeakAmplitude/instrumentIndex are constant for the whole note (same clip throughout), so
    // just capture them on any frame; peakDrive is the running max of envelopeValue/clipPeakAmplitude,
    // the same "how far into this note's own dynamic range are we" value CombinedResponder computes,
    // tracked here independently so this works whichever responder is actually active/selected.
    private float clipPeakAmplitude;
    private int instrumentIndex = -1;
    private float peakDrive;

    // -- Added for Onset/STFT timbre-robustness checks against the actual four game instruments
    // (previously only covered by Figure 3.1's synth-preset test for onset, and not at all for STFT).
    [Header("Onset/STFT band split (mirrors STFTResponder/CombinedResponder -- keep these two in sync " +
             "if you ever retune the responders' band cutoffs)")]
    [SerializeField] private float bassMaxHz = 250f;
    [SerializeField] private float midMaxHz = 2000f;

    // Onset: latency from note start to the first frame OnsetDetector actually fires, and whether it
    // ever fired at all within this tap -- both are the metrics Figure 3.1 already reports for the
    // synth presets (threshold-crossing time), now measurable against the real game instruments via
    // the real OnsetDetector rather than a fixed envelope threshold.
    private bool onsetEverDetected;
    private float onsetLatencySeconds;
    private float peakOnsetStrength;

    // STFT: same ratio-based band split as STFTResponder (each band normalized against that frame's
    // own rmsValue, so it's timbre-relative rather than absolute) -- peak mid/high energy reached
    // during the note, per instrument.
    private float peakStftMid;
    private float peakStftHigh;

    private Dictionary<int, (float dom, float rms, float[] chroma, int chromaPc)>
        sampledFrames = new Dictionary<int, (float, float, float[], int)>();

    // ── Analysis-derived duration tracking ──────────────────────────────
    // First and last frame (by dspTime) where envelopeValue exceeded
    // envelopeThreshold for the current note. This is independent of
    // AudioSource.isPlaying/timeSamples -- it comes purely from the
    // extracted envelope signal, so it's the thing to compare against
    // expectedDuration when evaluating whether audio analysis can
    // accurately recover note duration, as opposed to `duration`, which
    // just reflects Unity's own playback-state timing.
    private double analysisOnsetDspTime = -1;
    private double analysisLastAboveDspTime = -1;
    private bool analysisEverAboveThreshold = false;

    // ── Time-series mode state ─────────────────────────────────────────
    private bool tsRecording = false;
    private string tsCsvPath;
    private readonly StringBuilder tsBuffer = new StringBuilder();
    private int tsFrameCount = 0;
    private static readonly string[] NOTE_NAMES =
        { "C","Cs","D","Ds","E","F","Fs","G","Gs","A","As","B" };

    private void Start()
    {
        EvaluationSession.EnsureInitialised(sampleFrames);
        var nav = GetComponent<NoteAudioVisualizer>();
        if (nav != null)
        {
            nav.SetEvaluationLogger(this);
            Debug.Log($"[EvaluationLogger] Registered on '{gameObject.name}'");
        }
        else
        {
            Debug.LogWarning($"[EvaluationLogger] No NoteAudioVisualizer on '{gameObject.name}'");
        }
    }

    // ── Called every playing frame ──────────────────────────────────────
    public void RecordPlayingFrame(in AudioFeatureFrame frame, int currentFrameNumber)
    {
        frameCount = currentFrameNumber;

        // Normal mode accumulation
        if (isRecording)
        {
            if (frame.rmsValue > peakRms) peakRms = frame.rmsValue;
            if (frame.envelopeValue > peakEnvelope) peakEnvelope = frame.envelopeValue;
            if (frame.dominantFrequencyHz > peakDominantHz) peakDominantHz = frame.dominantFrequencyHz;

            // Constant for the whole note -- just take whatever the current frame reports.
            if (frame.clipPeakAmplitude > 0f) clipPeakAmplitude = frame.clipPeakAmplitude;
            instrumentIndex = frame.instrumentIndex;

            float rawEnvelope = frame.envelopeValue > 0f ? frame.envelopeValue : frame.rmsValue;
            float drive = frame.clipPeakAmplitude > 0.0001f
                ? Mathf.Clamp01(rawEnvelope / frame.clipPeakAmplitude)
                : 0f;
            if (drive > peakDrive) peakDrive = drive;

            // --- Onset latency/strength ---
            if (frame.onsetDetected && !onsetEverDetected)
            {
                onsetEverDetected = true;
                onsetLatencySeconds = (float)(frame.dspTime - onsetDspTime);
            }
            if (frame.onsetStrength > peakOnsetStrength) peakOnsetStrength = frame.onsetStrength;

            // --- STFT band energy (same ratio-based split as STFTResponder, timbre-relative since
            // each band is normalized against this frame's own RMS, not an absolute magnitude) ---
            if (frame.stftMagnitudeFrames != null)
            {
                int nF = frame.stftMagnitudeFrames.GetLength(0);
                int nB = frame.stftMagnitudeFrames.GetLength(1);
                int last = nF - 1;
                float sr = frame.sampleRate > 0 ? frame.sampleRate : 44100f;
                float binHz = sr / (nB * 2f);
                float norm = frame.rmsValue > 0.0001f ? frame.rmsValue : 1f;

                float midSum = 0f; int midN = 0;
                float highSum = 0f; int highN = 0;
                for (int k = 0; k < nB; k++)
                {
                    float hz = k * binHz;
                    if (hz < bassMaxHz) continue; // bass excluded, same as STFTResponder/CombinedResponder
                    float mag = frame.stftMagnitudeFrames[last, k];
                    if (hz < midMaxHz) { midSum += mag; midN++; }
                    else { highSum += mag; highN++; }
                }
                float midAvg = Mathf.Clamp01(midN > 0 ? midSum / midN / norm : 0f);
                float highAvg = Mathf.Clamp01(highN > 0 ? highSum / highN / norm : 0f);
                if (midAvg > peakStftMid) peakStftMid = midAvg;
                if (highAvg > peakStftHigh) peakStftHigh = highAvg;
            }

            // Analysis-derived duration tracking. The first frame the
            // envelope crosses envelopeThreshold is the analysis-detected
            // onset; the most recent frame still above threshold is kept
            // as the provisional offset and finalised in Flush().
            if (frame.envelopeValue > envelopeThreshold)
            {
                if (!analysisEverAboveThreshold)
                {
                    analysisOnsetDspTime = frame.dspTime;
                    analysisEverAboveThreshold = true;
                }
                analysisLastAboveDspTime = frame.dspTime;
            }

            foreach (int t in sampleFrames)
            {
                if (frameCount == t && !sampledFrames.ContainsKey(t))
                {
                    float[] chromaCopy = null;
                    if (frame.chromaVector != null)
                    {
                        chromaCopy = new float[12];
                        System.Array.Copy(frame.chromaVector, chromaCopy, 12);
                    }
                    sampledFrames[t] = (
                        frame.dominantFrequencyHz,
                        frame.rmsValue,
                        chromaCopy,
                        frame.estimatedPitchClass);
                }
            }
        }

        // Time-series mode: write every frame
        if (tsRecording)
            WriteTimeSeriesRow(in frame, currentFrameNumber);
    }

    // ── Called on note start/stop transitions ───────────────────────────
    public void RecordFrame(in AudioFeatureFrame frame, bool noteJustStarted,
                            bool noteStopped, string currentClip, int currentPitchClass,
                            float expectedDuration)
    {
        if (noteJustStarted)
        {
            if (isRecording) Flush();

            isRecording = true;
            frameCount = 0;
            onsetDspTime = AudioSettings.dspTime;
            clipName = currentClip;
            pitchClass = currentPitchClass;
            this.expectedDuration = expectedDuration;
            peakRms = 0f;
            peakEnvelope = 0f;
            peakDominantHz = 0f;
            clipPeakAmplitude = 0f;
            instrumentIndex = -1;
            peakDrive = 0f;
            onsetEverDetected = false;
            onsetLatencySeconds = -1f;
            peakOnsetStrength = 0f;
            peakStftMid = 0f;
            peakStftHigh = 0f;
            sampledFrames.Clear();

            // Reset analysis-derived duration tracking for the new note
            analysisOnsetDspTime = -1;
            analysisLastAboveDspTime = -1;
            analysisEverAboveThreshold = false;

            int tap = EvaluationSession.NextTap();
            Debug.Log($"[EvaluationLogger] '{gameObject.name}' tap #{tap} " +
                      $"started: note={currentClip}({currentPitchClass}), " +
                      $"expectedDuration={expectedDuration:F3}s");

            // Start time-series capture if requested
            if (captureTimeSeries)
            {
                StartTimeSeries(currentClip);
                captureTimeSeries = false; // auto-disable after one tap
                Debug.Log($"[EvaluationLogger] Time-series capture started -> {tsCsvPath}");
            }
        }

        if (noteStopped && isRecording)
        {
            Flush();
            if (tsRecording) FlushTimeSeries();
        }
    }

    // ── Normal mode flush ───────────────────────────────────────────────
    private void Flush()
    {
        float duration = (float)(AudioSettings.dspTime - onsetDspTime);

        // 0 if the envelope never crossed envelopeThreshold at all (e.g. a
        // very quiet note, or a threshold set too high for this instrument).
        float analysisDetectedDuration = analysisEverAboveThreshold
            ? (float)(analysisLastAboveDspTime - analysisOnsetDspTime)
            : 0f;

        Debug.Log($"[EvaluationLogger] writing row: note={clipName}, " +
                  $"duration={duration:F3}s, expected={expectedDuration:F3}s, " +
                  $"analysisDetected={analysisDetectedDuration:F3}s, " +
                  $"instrument={instrumentIndex}, clipPeak={clipPeakAmplitude:F4}, " +
                  $"peakDrive={peakDrive:F4}, onsetEverDetected={onsetEverDetected}, " +
                  $"onsetLatency={onsetLatencySeconds:F3}s, peakOnsetStrength={peakOnsetStrength:F4}, " +
                  $"peakStftMid={peakStftMid:F4}, peakStftHigh={peakStftHigh:F4}, " +
                  $"sampledFrames={sampledFrames.Count}");
        EvaluationSession.WriteRow(clipName, pitchClass, duration,
                                   expectedDuration, analysisDetectedDuration,
                                   peakRms, peakEnvelope, peakDominantHz,
                                   instrumentIndex, clipPeakAmplitude, peakDrive,
                                   onsetEverDetected, onsetLatencySeconds, peakOnsetStrength,
                                   peakStftMid, peakStftHigh,
                                   sampledFrames, sampleFrames);
        isRecording = false;
    }

    // ── Time-series mode ────────────────────────────────────────────────
    private void StartTimeSeries(string note)
    {
        tsRecording = true;
        tsFrameCount = 0;
        tsBuffer.Clear();

        string timestamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
        tsCsvPath = Path.Combine(Application.persistentDataPath,
                                 $"timeseries_{note}_{timestamp}.csv");

        // Header -- one column per STFT bin (512 bins for 1024-sample window)
        var ic = CultureInfo.InvariantCulture;
        tsBuffer.Append("frame,dspTime_s,rms,envelope,dominantHz," +
                        "onsetStrength,onsetDetected,chromaPc");
        foreach (string n in NOTE_NAMES)
            tsBuffer.Append($",chroma_{n}");
        // STFT: log most recent spectrum frame (numBins columns)
        // numBins = windowSize/2. With windowSize=1024, numBins=512.
        // Label bins by their center frequency at 44100Hz/1024 = 43.1Hz per bin
        for (int k = 0; k < 512; k++)
        {
            float freqHz = k * (44100f / 1024f);
            tsBuffer.Append($",stft_bin{k}_{freqHz:F0}hz");
        }
        tsBuffer.AppendLine();
    }

    private void WriteTimeSeriesRow(in AudioFeatureFrame frame, int frameNum)
    {
        var ic = CultureInfo.InvariantCulture;
        double elapsed = frame.dspTime - onsetDspTime;

        tsBuffer.Append($"{frameNum}");
        tsBuffer.Append($",{elapsed.ToString("F4", ic)}");
        tsBuffer.Append($",{frame.rmsValue.ToString("F5", ic)}");
        tsBuffer.Append($",{frame.envelopeValue.ToString("F5", ic)}");
        tsBuffer.Append($",{frame.dominantFrequencyHz.ToString("F1", ic)}");
        tsBuffer.Append($",{frame.onsetStrength.ToString("F5", ic)}");
        tsBuffer.Append($",{(frame.onsetDetected ? 1 : 0)}");
        tsBuffer.Append($",{frame.estimatedPitchClass}");

        if (frame.chromaVector != null)
            for (int i = 0; i < 12; i++)
                tsBuffer.Append($",{frame.chromaVector[i].ToString("F4", ic)}");
        else
            tsBuffer.Append(new string(',', 12));

        // Write most recent STFT frame (last row of stftMagnitudeFrames matrix)
        if (frame.stftMagnitudeFrames != null)
        {
            int numFrames = frame.stftMagnitudeFrames.GetLength(0);
            int numBins   = frame.stftMagnitudeFrames.GetLength(1);
            int lastFrame = numFrames - 1;
            for (int k = 0; k < Mathf.Min(numBins, 512); k++)
                tsBuffer.Append($",{frame.stftMagnitudeFrames[lastFrame, k].ToString("F6", ic)}");
        }
        else
            tsBuffer.Append(new string(',', 512));

        tsBuffer.AppendLine();
        tsFrameCount++;
    }

    private void FlushTimeSeries()
    {
        tsRecording = false;
        try
        {
            File.WriteAllText(tsCsvPath, tsBuffer.ToString());
            Debug.Log($"[EvaluationLogger] Time-series written: {tsFrameCount} frames -> {tsCsvPath}");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[EvaluationLogger] Time-series write failed: {e.Message}");
        }
        tsBuffer.Clear();
    }

    private void OnDestroy()
    {
        if (isRecording) Flush();
        if (tsRecording) FlushTimeSeries();
    }
}