# AudioVisualizer: Real-Time Music Visualization for an Interactive Music Learning App

Bachelor's thesis project, Media Informatics (B.Sc.), Hochschule der Medien Stuttgart.

**Thesis title:** *Konzeption und Evaluation audioanalytischer Verfahren zur Echtzeitvisualisierung musikalischer Parameter in einer interaktiven Musiklern-App*
(Concept and evaluation of audio analysis methods for real-time visualization of musical parameters in an interactive music learning app)

![Gameplay / demo GIF](Media/demo.gif)

<!-- Replace with your best GIF: the app reacting to live audio -->

## What is this project?

A Unity application that analyzes audio in real time and turns musical parameters (loudness, pitch, notes, onsets, timbre) into visual feedback. The goal is to support music learning: learners should *see* what they hear and play.

The thesis answers two questions:

1. Which audio analysis methods are suitable for real-time use in an interactive app, in terms of accuracy, latency and robustness?
2. How should the extracted parameters be mapped to visuals so that learners understand and enjoy them?

## Highlights

- Real-time audio analysis pipeline in C# / Unity
- Methods covered: **FFT / STFT**, **chroma features**, **RMS**, **spectral centroid**, **onset / beat detection**
- Audio-to-visual mapping for pitch classes, notes and loudness
- Technical evaluation: **latency**, **robustness against different timbres**, comparison of analysis methods
- User study with **UEQ** and **AttrakDiff** questionnaires, including results by experience level, color ratings and preferences

## How it works

```
Audio input  ->  Audio filtering  ->  Analysis  ->  Parameter mapping  ->  Real-time visuals
 (file / mic)    (AudioFilter)       (FFT, STFT,      (colors, shapes,       (Unity scene)
                                      chroma, RMS,     motion)
                                      onsets)
```

1. **Audio input and filtering.** [Describe input: audio file, microphone, ...]. The `AudioFilter` module [short description: e.g. preprocessing, band filtering, windowing].
2. **Analysis.** [Short description: e.g. the audio buffer is windowed and transformed with an FFT; from the spectrum, the dominant frequency, chroma vector and spectral centroid are derived; RMS gives loudness; onset detection reacts to note attacks.]
3. **Mapping.** [Short description: e.g. each of the 12 pitch classes gets a color, loudness controls size/intensity, onsets trigger visual events.]
4. **Rendering.** The `AudioVisualizer` Unity project renders the result every frame with low latency.

See the architecture diagram in [`Docs/fig_uml.pdf`](Docs/fig_uml.pdf) and the concept overview in [`Docs/fig_concept_overview.pdf`](Docs/fig_concept_overview.pdf).

## My role

[Solo project / Developed together with a teammate. Adjust as needed.]

- Designed the concept and the analysis pipeline
- Implemented the audio analysis and the visualization in Unity (C#)
- Built the evaluation setup (latency measurements, robustness tests)
- Planned and conducted the user study and analyzed the results
- Wrote the thesis (in English)

## Results (short version)

[Add 3-5 concrete findings here. This is the most valuable part for recruiters. Examples of the format:]

- Onset detection latency: [X ms on average]
- Most robust method across different timbres: [method], see [`Docs/timbre_robustness_v3.pdf`](Docs/timbre_robustness_v3.pdf)
- User study ([N] participants): [e.g. visualization rated as helpful by X%, differences between beginners and experienced players]

## Repository structure

| Folder | Content |
|---|---|
| `AudioVisualizer/` | Unity project code (scripts written by me) |
| `AudioFilter/` | Audio filtering / preprocessing module |
| `Evaluation/` | Evaluation scripts and data for latency, robustness and the user study |
| `Docs/` | Thesis PDF and figures (UML, spectrograms, survey results, ...) |
| `Media/` | GIFs and screenshots |

## Documentation

- [Full Bachelor's thesis (PDF)](Docs/Bachelorarbeit_Erik_26_08_26_unsigned.pdf)
- Selected figures: [UML](Docs/fig_uml.pdf), [STFT spectrogram](Docs/fig_stft_spectrogram.pdf), [Onset latency](Docs/fig_onset_latency.pdf), [Survey preferences](Docs/fig_survey_preferences.pdf)

## Try it

- **Build (Windows):** [Download from Releases](https://github.com/Oertschl/Portfolio/releases) <!-- add link after creating the release -->
- **Video:** [Watch on YouTube](https://www.youtube.com/watch?v=YOUR_VIDEO_ID)
- **Run from source:** [Unity version], open the `AudioVisualizer` folder as a project, open the scene `[SceneName]`, press Play.

## Tech stack

Unity [version] · C# · Digital signal processing (FFT, STFT, chroma, RMS) · LaTeX · UEQ / AttrakDiff

## Contact

Erik · [LinkedIn](#) · [ArtStation](#) · [Email](#)