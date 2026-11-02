# Changelog

## 1.1.0 — 2026-11-02

Initial public release.

- Portable Windows x64 recorder with a console-controlled detached worker; closing the console stops collection and completes report generation.
- PresentMon 2.6.0 frame capture aligned with system, GPU, process and scheduling-probe telemetry.
- Offline HTML timeline, per-swap-chain candidate detection, correlation details and JSON summaries.
- Optional bounded WPR/ETW recording for CPU waits/stacks, DPC/ISR, graphics/composition and file/disk/hard-fault activity.
- Automatic long-frame ETL snapshots, named-instance isolation, export interval annotations and event-loss reporting.
- Source-only Git repository, hash-verified dependency download, allowlisted release packaging and Windows CI.

Windows 11 x64 has been tested on one physical machine. Windows 10, ARM64, every game/anti-cheat combination and other hardware configurations have not been comprehensively validated.
