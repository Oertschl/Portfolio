using UnityEngine;

/// <summary>
/// Magnitude-spectrum FFT extractor using a radix-2 Cooley-Tukey algorithm
/// with a Hann window.
///
/// Writes into AudioFeatureFrame:
///   - spectrumMagnitudes   float[], length windowSize/2 (positive frequencies only)
///   - dominantFrequencyHz  float   -- bin with highest magnitude, mapped to Hz
///   - spectralCentroid     float   -- weighted mean frequency, maps to "brightness"
///
/// Why implement FFT here rather than use Unity's GetSpectrumData:
///   - All extractors share one raw PCM input (OnAudioFilterRead), so results
///     are comparable on identical data -- required for thesis evaluation fairness.
///   - GetSpectrumData uses Unity's own fixed window/algorithm with no control
///     over windowing, making STFT/chroma extraction from it unreliable.
///   - This implementation gives you the complex output needed for STFT framing
///     later, not just magnitudes.
/// </summary>
public class FFTExtractor : IAudioFeatureExtractor
{
    public string MethodName => $"FFT (Hann window, size={windowSize})";

    private readonly int windowSize;
    public int RequiredSampleCount => windowSize;

    // Pre-computed Hann window coefficients -- computed once at construction
    // rather than every Process() call, since the window shape is fixed.
    private readonly float[] hannWindow;

    // Reusable working buffers -- pre-allocated to avoid per-frame GC pressure.
    // FFT works in-place on real/imaginary parts as separate float arrays.
    private readonly float[] real;
    private readonly float[] imag;

    public FFTExtractor(int windowSize = 1024)
    {
        // Radix-2 FFT requires power-of-two window size.
        // Clamp to nearest lower power of two if needed.
        this.windowSize = Mathf.NextPowerOfTwo(windowSize);
        if (this.windowSize != windowSize)
        {
            Debug.LogWarning($"[FFTExtractor] windowSize {windowSize} is not a power of two. " +
                             $"Clamping to {this.windowSize}.");
        }

        hannWindow = new float[this.windowSize];
        real = new float[this.windowSize];
        imag = new float[this.windowSize];

        // Hann window: w(n) = 0.5 * (1 - cos(2*pi*n / (N-1)))
        // Tapers samples at the edges of the buffer toward zero, which
        // reduces "spectral leakage" -- the smearing of energy from one
        // frequency bin into neighboring bins that happens when the signal
        // doesn't complete a whole number of cycles within the window.
        for (int n = 0; n < this.windowSize; n++)
        {
            hannWindow[n] = 0.5f * (1f - Mathf.Cos(2f * Mathf.PI * n / (this.windowSize - 1)));
        }
    }

    public void Process(in AudioBuffer buffer, ref AudioFeatureFrame frame)
    {
        if (buffer.samples.Length < RequiredSampleCount)
            return;

        int n = windowSize;

        // --- Step 1: Apply Hann window to samples ---
        // Copy windowed samples into real[], zero out imag[].
        // We only use the most recent `windowSize` samples from the buffer.
        int offset = buffer.samples.Length - n; // take from end of buffer
        for (int i = 0; i < n; i++)
        {
            real[i] = buffer.samples[offset + i] * hannWindow[i];
            imag[i] = 0f;
        }

        // --- Step 2: In-place radix-2 Cooley-Tukey FFT ---
        // Standard iterative implementation. Operates on real[]/imag[] in place.
        // Result: real[k] + j*imag[k] = X(k), the DFT of the windowed signal.
        FFT(real, imag, n);

        // --- Step 3: Compute magnitude spectrum (positive frequencies only) ---
        // The DFT of a real signal is symmetric: X(N-k) = X(k)*.
        // So only bins 0..N/2 carry unique information.
        int numBins = n / 2;
        float binToHz = (float)buffer.sampleRate / n;

        float[] magnitudes = new float[numBins];
        float maxMagnitude = 0f;
        int dominantBin = 0;

        float weightedFreqSum = 0f;
        float magnitudeSum = 0f;

        for (int k = 0; k < numBins; k++)
        {
            float magnitude = Mathf.Sqrt(real[k] * real[k] + imag[k] * imag[k]);

            // Normalize by window size so magnitude is independent of N.
            // Multiply non-DC bins by 2 to account for the discarded mirror.
            magnitude = magnitude / n * (k == 0 ? 1f : 2f);

            magnitudes[k] = magnitude;

            if (magnitude > maxMagnitude)
            {
                maxMagnitude = magnitude;
                dominantBin = k;
            }

            // Accumulate for spectral centroid: sum(freq * magnitude) / sum(magnitude)
            float freqHz = k * binToHz;
            weightedFreqSum += freqHz * magnitude;
            magnitudeSum += magnitude;
        }

        // --- Step 4: Write results into frame ---
        frame.spectrumMagnitudes = magnitudes;
        frame.dominantFrequencyHz = dominantBin * binToHz;
        frame.spectralCentroid = magnitudeSum > 0f ? weightedFreqSum / magnitudeSum : 0f;
        frame.dspTime = buffer.dspTime;
        frame.sourceId = buffer.sourceId;
    }

    /// <summary>
    /// Iterative in-place radix-2 Cooley-Tukey FFT.
    /// Input: real[], imag[] of length n (must be power of two).
    /// Output: real[], imag[] contain the complex DFT coefficients X(k).
    /// </summary>
    private static void FFT(float[] re, float[] im, int n)
    {
        // --- Bit-reversal permutation ---
        // Reorders input samples into the order the butterfly stages expect.
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

        // --- Butterfly stages ---
        // Each stage combines pairs of DFT results into larger DFTs.
        // log2(N) stages total.
        for (int len = 2; len <= n; len <<= 1)
        {
            // Twiddle factor angle for this stage: w = e^(-j*2*pi/len)
            float angle = -2f * Mathf.PI / len;
            float wRe = Mathf.Cos(angle);
            float wIm = Mathf.Sin(angle);

            for (int i = 0; i < n; i += len)
            {
                float curRe = 1f;
                float curIm = 0f;

                for (int j = 0; j < len / 2; j++)
                {
                    // Butterfly: combines element at i+j with element at i+j+len/2
                    int u = i + j;
                    int v = i + j + len / 2;

                    // t = w * X[v]  (twiddle multiplication)
                    float tRe = curRe * re[v] - curIm * im[v];
                    float tIm = curRe * im[v] + curIm * re[v];

                    // X[u] = X[u] + t
                    // X[v] = X[u] - t
                    re[v] = re[u] - tRe;
                    im[v] = im[u] - tIm;
                    re[u] = re[u] + tRe;
                    im[u] = im[u] + tIm;

                    // Advance twiddle factor: cur *= w
                    float nextRe = curRe * wRe - curIm * wIm;
                    float curImNew = curRe * wIm + curIm * wRe;
                    curRe = nextRe;
                    curIm = curImNew;
                }
            }
        }
    }

    /// <summary>
    /// Reverses the bottom `bits` bits of an integer.
    /// Used by the bit-reversal permutation stage of the FFT.
    /// e.g. BitReverse(0b0110, 4) = 0b0110 reversed = 0b0110
    /// </summary>
    private static int BitReverse(int n, int bits)
    {
        int reversed = 0;
        for (int i = 0; i < bits; i++)
        {
            reversed = (reversed << 1) | (n & 1);
            n >>= 1;
        }
        return reversed;
    }
}