# Real-Time Audio Analysis & Visualization for a Music Learning App

**Bachelor's thesis** · Media Informatics (B.Sc.) · Hochschule der Medien Stuttgart · 2026
**Title:** *Design and Evaluation of Audio-Analytical Methods for Real-Time Visualization of Musical Parameters in an Interactive Music Learning Application*

![Demo GIF](Media/demo.gif)

<!-- Replace with your best GIF: the particle visualization reacting to notes in Tone -->

## TL;DR

I extended **Tone**, a Unity-based music learning app, with a modular system that analyzes audio in real time and turns it into particle-based visual feedback. I implemented **five audio analysis methods** from scratch in C#, compared them technically (accuracy, timbre robustness, real-time cost) and tested the result in a **within-subjects user study (n = 15)**.

**Main finding:** no single analysis method is accurate enough to drive pitch-based visuals once the instrument changes. Real-time performance was never the problem: all methods run far inside the audio time budget. So the final system does not rely on any single detected value.

> Tone is the PhD project of Willi Schorrig (second examiner of the thesis), used here as the framework for my work. This repository contains **only the code I wrote**, not the Tone app itself.

## What I built

| Component | What it does |
|---|---|
| **Audio capture** | Captures PCM audio on Unity's audio thread (`OnAudioFilterRead`) and hands it to the main thread through a **double buffer** with a minimal lock |
| **Five feature extractors** | RMS/envelope following, FFT dominant-frequency picking, chroma features, STFT band energy, onset detection (spectral flux). All behind one `IAudioFeatureExtractor` interface |
| **Shared data contracts** | `AudioBuffer` in, `AudioFeatureFrame` out. Every method reads identical samples, so differences in results come from the method, not the input |
| **Six visual responders** | One isolated `IVisualResponder` per method for controlled comparison, plus a `CombinedResponder` that fuses several signals for actual gameplay |
| **Particle visualization** | A single shared Unity `ParticleSystem` per note block (size, speed, density, lifetime, hue) driven by the active responder |
| **Evaluation tooling** | `EvaluationLogger` / `EvaluationSession` write per-note and per-frame CSV data; a session manager sends study data to a Google Sheet |

## How it works

```
Unity audio thread                      Unity main thread
┌────────────────────┐   double buffer  ┌──────────────────────────────────────────────┐
│ OnAudioFilterRead  │ ───────────────► │ AudioBuffer ─► 5 extractors ─► AudioFeature- │
│ (PCM capture only) │   (lock ~µs)     │ (+ dspTime)    (RMS, FFT,      Frame         │
└────────────────────┘                  │                chroma, STFT,       │         │
                                        │                onset)              ▼         │
                                        │                         IVisualResponder     │
                                        │                                    │         │
                                        │                         ParticleSystem       │
                                        │                                    ▼         │
                                        │                         EvaluationLogger ─► CSV │
                                        └──────────────────────────────────────────────┘
```

Design decisions worth mentioning:

- **Audio thread does the minimum.** It only mono-sums samples and copies them under a lock. All heavy work (FFT etc.) runs on the main thread, so there are no audio dropouts.
- **Own FFT implementation.** A radix-2 Cooley-Tukey FFT with Hann window, written from scratch so every extractor windows and transforms the same samples.
- **Sample-accurate timestamps.** `AudioSettings.dspTime` is captured once per audio callback and shared by all extractors, to align audio events with rendered frames.
- **Swappable methods.** Switching the active responder is an Inspector setting. All extractors keep running either way, so logging stays comparable.
- **Reactive, not triggered.** The visualizer listens to what the `AudioSource` actually outputs instead of reacting to the `Play()` call, which fixed a timing desync. This includes detecting note start, stop and retriggers from `isPlaying` / `timeSamples`.
- **Performance work:** idle skip for notes that are not playing, pre-allocated FFT buffers, and a static per-clip analysis cache shared across all instances.

Full UML diagram: [`Docs/fig_uml.pdf`](Docs/fig_uml.pdf) · Concept overview: [`Docs/fig_concept_overview.pdf`](Docs/fig_concept_overview.pdf)

## Results

### Technical evaluation

| Question | Result |
|---|---|
| **Is it fast enough?** | Yes. The audio-thread lock took **0.77 µs on average** against a **23,220 µs** buffer deadline. Even the worst spike (3,677 µs, attributed to OS scheduling) was 15.8% of the budget. No dropouts. |
| **FFT peak picking** | Unreliable. On marimba, note C was misidentified with **+78.9% error** (second harmonic dominates). Other notes were about 10% low from bin quantization. |
| **Chroma features** | Best pitch method, but not robust. Correct on 2 of 4 marimba notes. Across instruments, the same note D was detected as D# on marimba and guitar. |
| **Onset detection** | The only timbre-robust method: **100% detection on all four instruments**, frame-exact (0 ms) on almost every tap. It carries no pitch information. |
| **Loudness normalization** | Normalizing per clip reduced cross-instrument loudness variation from a CV of 24.1% to 21.7%. It helps but does not equalize. |

Based on this, the `CombinedResponder` takes **color from the known pitch class** instead of a detected value, and uses a normalized loudness "drive" plus onset bursts for size and speed.

### User study (within-subjects, n = 15)

Old static visuals vs. the new particle system, alternating per level, compared on understandability, perceived synchronicity and overall experience.

- **Pitch identification:** 9 of 15 preferred the new system, 2 the old one
- **Perceived sync:** 7 preferred the old system, 1 the new one (discussed in the thesis: the old visual is not derived from audio, so it cannot lag)
- **Enjoyment:** split evenly (7 vs 7). **Overall preference:** 7 Old vs 6 New, with strongly held opinions on both sides
- **Color:** helped identify *which* note was played and was not seen as distracting, but participants were unsure it was necessary

The thesis also discusses the limits honestly: small convenience sample, partly remote testing, no no-visual baseline, and some pitch errors likely caused by the pitch-shifted marimba samples rather than the methods themselves.

**Selected figures:**
[STFT spectrogram](Docs/fig_stft_spectrogram.pdf) ·
[Chroma vectors](Docs/fig_chroma_vectors.pdf) ·
[Onset latency](Docs/fig_onset_latency.pdf) ·
[Lock timing](Docs/fig_lock_timing.pdf) ·
[Timbre robustness](Docs/timbre_robustness_v3.pdf) ·
[Survey preferences](Docs/fig_survey_preferences.pdf) ·
[Survey by experience](Docs/fig_survey_by_experience.pdf)

## Repository structure

| Folder | Content |
|---|---|
| `AudioFilter/` | Audio analysis layer: `AudioBuffer`, `AudioFeatureFrame`, the `IAudioFeatureExtractor` interface and the five extractors (`RmsEnvelopeFollower`, `FFTExtractor`, `ChromaExtractor`, `STFTExtractor`, `OnsetDetector`) |
| `AudioVisualizer/` | Visualization layer: note visualizer, responders, particle system control and evaluation logging |
| `Evaluation/` | Raw evaluation data and scripts used for the thesis figures |
| `Docs/` | Thesis PDF and all figures |
| `Media/` | GIFs and screenshots |

## Try it

- **Playable builds (Windows and Android):** [Download from Releases](https://github.com/Oertschl/Portfolio/releases) <!-- add link after creating the release -->
- **Thesis (PDF):** [Read the full thesis](Docs/Bachelorarbeit_Erik_26_08_26_unsigned.pdf)

## Tech stack

Unity 2022.3.18f1 · C# · Digital signal processing (FFT, STFT, chroma, RMS, spectral flux) · Multithreading (audio thread / main thread, double buffering) · CSV data logging · Google Forms / Apps Script · User research (within-subjects study) · LaTeX

## Contact

Erik Lindinger · [LinkedIn](#) · [ArtStation](#) · [Email](#)