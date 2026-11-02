# TMODE

A portable Windows optimization station for competitive Fortnite players.

**[Download the latest release](https://github.com/koolkatgaming123-collab/TMODE/releases/latest)**

- **Tweak PC:** per-PC scan, exact change review, verified Windows restore point, supported performance changes and recorded recovery.
- **Strip Windows ⚠:** disable selected optional features without uninstalling; repair each feature independently.
- **Internet Optimizer:** bounded hardware-aware RSS/NIC comparisons, repeated baseline/treatment/baseline measurements and exact restoration.
- **Guide / BIOS:** hardware-specific advice with primary references; firmware changes remain manual.
- **Experimental:** optional switches start off.
- **Updates:** remote signed manifests and an explicit download/restart flow in the same EXE.

Read [README.txt](README.txt) for use, supported actions and recovery, and
[VALIDATION.txt](VALIDATION.txt) for observed checks and limits.

The native Internet capture measures ping/jitter/loss, with processing latency
explicitly unknown. It cannot establish Fortnite server ping or an absolute
optimum. Frame-rate, endgame and input-latency improvements need representative
measurements; this release does not promise a universal gain.

## Build

Use the .NET 10 SDK on Windows: `./build.ps1`.
The self-contained x64 app needs no separately installed .NET runtime.

`--self-test <output.txt>` runs isolated regression fixtures.
`--preview-dialogs <directory>` renders hidden synthetic UI checks.
Neither command is a live tuning or Fortnite benchmark command.

Publisher instructions: [release/UPDATE-PUBLISHING.txt](release/UPDATE-PUBLISHING.txt).
Third-party information: [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
