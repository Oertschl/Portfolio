using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// Singleton that owns the CSV file, tap counter, and header for the
/// entire Play session. All EvaluationLogger instances on all ToneBlocks
/// report here, so multiple simultaneous recorders don't interfere.
///
/// No MonoBehaviour needed -- purely static, reset each Play session
/// via RuntimeInitializeOnLoadMethod.
/// </summary>
public static class EvaluationSession
{
    private static string csvPath;
    private static int tapCount = 0;
    private static bool initialised = false;
    private static int[] sessionSampleFrames;
    private static readonly object fileLock = new object();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Reset()
    {
        csvPath = null;
        tapCount = 0;
        initialised = false;
        sessionSampleFrames = null;
    }

    public static void EnsureInitialised(int[] sampleFrames)
    {
        if (initialised) return;

        string timestamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
        csvPath = Path.Combine(Application.persistentDataPath, $"eval_{timestamp}.csv");
        sessionSampleFrames = sampleFrames;
        initialised = true;

        WriteHeader();
        Debug.Log($"[EvaluationSession] CSV: {csvPath}");
    }

    /// <summary>Increments and returns the new tap number. Thread-safe.</summary>
    public static int NextTap()
    {
        lock (fileLock)
        {
            return ++tapCount;
        }
    }

    // Mirrors ToneGameManager.INSTRUMENTS (Marimba=0, Guitar=1, Percussion=2, Bass=3) -- kept as a
    // local copy rather than referencing the enum directly so this file has no dependency on
    // ToneGameManager; update this array if that enum ever changes.
    private static readonly string[] INSTRUMENT_NAMES = { "Marimba", "Guitar", "Percussion", "Bass" };

    public static void WriteRow(
        string note, int pitchClass, float duration,
        float expectedDuration, float analysisDetectedDuration,
        float peakRms, float peakEnvelope, float peakDominantHz,
        int instrumentIndex, float clipPeakAmplitude, float peakDrive,
        bool onsetEverDetected, float onsetLatencySeconds, float peakOnsetStrength,
        float peakStftMid, float peakStftHigh,
        Dictionary<int, (float dom, float rms, float[] chroma, int chromaPc)> sampledFrames,
        int[] sampleFrames)
    {
        if (!initialised || csvPath == null) return;

        var ic = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();

        string instrumentName = (instrumentIndex >= 0 && instrumentIndex < INSTRUMENT_NAMES.Length)
            ? INSTRUMENT_NAMES[instrumentIndex]
            : "Unknown";

        sb.Append($"{tapCount},{note},{pitchClass}");
        sb.Append($",{duration.ToString("F3", ic)}");
        sb.Append($",{expectedDuration.ToString("F3", ic)}");
        sb.Append($",{analysisDetectedDuration.ToString("F3", ic)}");
        sb.Append($",{peakRms.ToString("F5", ic)}");
        sb.Append($",{peakEnvelope.ToString("F5", ic)}");
        sb.Append($",{peakDominantHz.ToString("F1", ic)}");
        sb.Append($",{instrumentIndex},{instrumentName}");
        sb.Append($",{clipPeakAmplitude.ToString("F5", ic)}");
        sb.Append($",{peakDrive.ToString("F5", ic)}");
        sb.Append($",{(onsetEverDetected ? 1 : 0)}");
        sb.Append($",{onsetLatencySeconds.ToString("F4", ic)}");
        sb.Append($",{peakOnsetStrength.ToString("F5", ic)}");
        sb.Append($",{peakStftMid.ToString("F5", ic)}");
        sb.Append($",{peakStftHigh.ToString("F5", ic)}");

        foreach (int f in sampleFrames)
        {
            if (sampledFrames.TryGetValue(f, out var s))
            {
                sb.Append($",{s.dom.ToString("F1", ic)},{s.rms.ToString("F5", ic)}");
                sb.Append($",{s.chromaPc}");
                if (s.chroma != null)
                    for (int i = 0; i < 12; i++)
                        sb.Append($",{s.chroma[i].ToString("F4", ic)}");
                else
                    sb.Append(new string(',', 12));
            }
            else
            {
                sb.Append(new string(',', 15)); // dom,rms,chromaPc + 12 chroma bins
            }
        }
        sb.AppendLine();

        lock (fileLock)
        {
            try
            {
                File.AppendAllText(csvPath, sb.ToString());
                Debug.Log($"[EvaluationSession] Tap #{tapCount} ({note}) written to CSV");
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[EvaluationSession] Write failed: {e.Message}");
            }
        }
    }

    private static void WriteHeader()
    {
        if (sessionSampleFrames == null) return;

        var sb = new StringBuilder();
        sb.Append("tap,note,pitchClass,duration_s,expectedDuration_s,analysisDetectedDuration_s," +
                  "peakRms,peakEnvelope,peakDominantHz," +
                  "instrumentIndex,instrument,clipPeakAmplitude,peakDrive," +
                  "onsetEverDetected,onsetLatency_s,peakOnsetStrength,peakStftMid,peakStftHigh");
        string[] noteNames = {"C","Cs","D","Ds","E","F","Fs","G","Gs","A","As","B"};
        foreach (int f in sessionSampleFrames)
        {
            sb.Append($",dominantHz_f{f},rms_f{f},chromaPc_f{f}");
            foreach (string n in noteNames)
                sb.Append($",chroma_{n}_f{f}");
        }
        sb.AppendLine();

        try
        {
            File.WriteAllText(csvPath, sb.ToString());
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[EvaluationSession] Header write failed: {e.Message}");
        }
    }
}