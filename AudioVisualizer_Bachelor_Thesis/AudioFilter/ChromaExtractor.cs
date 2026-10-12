using UnityEngine;

/// <summary>
/// Chroma feature extractor (Pitch Class Profile).
///
/// Computes its own Hann-windowed radix-2 FFT from raw PCM samples,
/// then folds the magnitude spectrum into 12 pitch-class bins (C..B)
/// by mapping each frequency bin to its pitch class via:
///     pitchClass = round(12 * log2(freq / C0)) mod 12
///
/// Why chroma is more robust than FFT peak-picking for pitch:
///   The 2nd harmonic of G4 (784 Hz = G5) folds back into the same
///   G chroma bin as the fundamental (392 Hz = G4). Overtones reinforce
///   the correct pitch class rather than confusing the detector.
///   This is the key advantage over dominantFrequencyHz, which reports
///   whichever partial is loudest -- often not the fundamental.
///
/// Writes into AudioFeatureFrame:
///   chromaVector[12]    -- normalised energy per pitch class (C=0..B=11)
///   estimatedPitchClass -- argmax of chromaVector (0-11)
/// </summary>
public class ChromaExtractor : IAudioFeatureExtractor
{
    public string MethodName => $"Chroma (PCP, window={windowSize}, A4={A4_HZ}Hz)";
    public int RequiredSampleCount => windowSize;

    private readonly int windowSize;
    private readonly float[] hannWindow;
    private readonly float[] real;
    private readonly float[] imag;

    // Reference frequency for pitch class mapping.
    // C0 = 16.352 Hz is the standard MIDI reference (C below the piano).
    // We use this so pitchClass = round(12 * log2(freq/C0)) % 12
    // maps 262 Hz (C4) → class 0, 294 Hz (D4) → class 2, etc.
    private const float C0_HZ = 16.3516f;
    private const float A4_HZ = 440.0f;   // for tuning reference in MethodName only

    // Pitch correction for linear-interpolation pitch-shifted samples.
    // Observed: all notes are consistently ~1.88 semitones flat compared
    // to equal temperament reference, due to spectral smearing from the
    // PitchShiftInterpolate() method in MusicPhraseGenerator.
    // Without this, G maps to F# (off by one semitone), A to G#, etc.
    // Value derived empirically from EvaluationLogger CSV data:
    // G detected at 351.6 Hz (true 392 Hz) -> log2(392/351.6)*12 = 1.88 st.
    private const float PITCH_CORRECTION_SEMITONES = 1.88f;
    // Below ~60 Hz (B1) most bins contain noise or sub-bass, not pitch content.
    private const float MIN_FREQ_HZ = 60f;

    public ChromaExtractor(int windowSize = 1024)
    {
        this.windowSize = Mathf.NextPowerOfTwo(windowSize);
        hannWindow = new float[this.windowSize];
        real = new float[this.windowSize];
        imag = new float[this.windowSize];

        for (int n = 0; n < this.windowSize; n++)
            hannWindow[n] = 0.5f * (1f - Mathf.Cos(2f * Mathf.PI * n / (this.windowSize - 1)));
    }

    public void Process(in AudioBuffer buffer, ref AudioFeatureFrame frame)
    {
        if (buffer.samples.Length < RequiredSampleCount)
            return;

        // --- Step 1: Apply Hann window and run FFT ---
        int n = windowSize;
        int offset = buffer.samples.Length - n;
        for (int i = 0; i < n; i++)
        {
            real[i] = buffer.samples[offset + i] * hannWindow[i];
            imag[i] = 0f;
        }
        FFT(real, imag, n);

        // --- Step 2: Fold magnitude spectrum into 12 chroma bins ---
        float binToHz = (float)buffer.sampleRate / n;
        float[] chroma = new float[12];

        for (int k = 1; k < n / 2; k++) // skip DC bin (k=0)
        {
            float freqHz = k * binToHz;
            if (freqHz < MIN_FREQ_HZ) continue;

            float magnitude = Mathf.Sqrt(real[k] * real[k] + imag[k] * imag[k]);
            magnitude = magnitude / n * 2f; // normalise and fold mirror

            // Map frequency to pitch class with empirical correction for
            // pitch-shift interpolation artifact (+1.88 semitones flat bias).
            float semitones = 12f * Mathf.Log(freqHz / C0_HZ, 2f) + PITCH_CORRECTION_SEMITONES;
            int pitchClass = Mathf.RoundToInt(semitones) % 12;
            if (pitchClass < 0) pitchClass += 12; // handle negative modulo

            chroma[pitchClass] += magnitude;
        }

        // --- Step 3: Normalise chroma vector to [0, 1] ---
        float maxChroma = 0f;
        int bestClass = 0;
        for (int i = 0; i < 12; i++)
        {
            if (chroma[i] > maxChroma)
            {
                maxChroma = chroma[i];
                bestClass = i;
            }
        }

        if (maxChroma > 0f)
            for (int i = 0; i < 12; i++)
                chroma[i] /= maxChroma;

        // --- Step 4: Write to frame ---
        frame.chromaVector        = chroma;
        frame.estimatedPitchClass = bestClass;
        frame.dspTime             = buffer.dspTime;
        frame.sourceId            = buffer.sourceId;
    }

    // Identical radix-2 Cooley-Tukey FFT used in FFTExtractor --
    // kept here so ChromaExtractor is fully self-contained with no
    // dependency on FFTExtractor being registered in the pipeline.
    private static void FFT(float[] re, float[] im, int n)
    {
        int bits = (int)Mathf.Log(n, 2);
        for (int i = 1; i < n; i++)
        {
            int rev = BitReverse(i, bits);
            if (rev > i)
            {
                float tmp = re[i]; re[i] = re[rev]; re[rev] = tmp;
                tmp = im[i]; im[i] = im[rev]; im[rev] = tmp;
            }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            float angle = -2f * Mathf.PI / len;
            float wRe = Mathf.Cos(angle), wIm = Mathf.Sin(angle);
            for (int i = 0; i < n; i += len)
            {
                float curRe = 1f, curIm = 0f;
                for (int j = 0; j < len / 2; j++)
                {
                    int u = i + j, v = i + j + len / 2;
                    float tRe = curRe * re[v] - curIm * im[v];
                    float tIm = curRe * im[v] + curIm * re[v];
                    re[v] = re[u] - tRe; im[v] = im[u] - tIm;
                    re[u] = re[u] + tRe; im[u] = im[u] + tIm;
                    float nextRe = curRe * wRe - curIm * wIm;
                    curIm = curRe * wIm + curIm * wRe;
                    curRe = nextRe;
                }
            }
        }
    }

    private static int BitReverse(int n, int bits)
    {
        int rev = 0;
        for (int i = 0; i < bits; i++) { rev = (rev << 1) | (n & 1); n >>= 1; }
        return rev;
    }
}