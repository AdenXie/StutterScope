# Changelog

## 1.2.0 — 2026-11-11

- One capture mode. `开始记录.cmd` always records frames and system telemetry and turns event-level WPR tracing on whenever `wpr.exe`, the profile and 3 GiB of free disk are available; otherwise it falls back and says why. `--light` opts out. `开始深度诊断.cmd` is removed; `--deep` is still accepted.
- Saved ETLs are decoded automatically through the ETW consumer API (no WPA or symbols): per-thread run/ready/blocked time for the game's busiest threads during the long frame, where they waited and which process woke them (module level), per-process CPU versus the preceding second, DPC/ISR time by driver module and hard faults. `--etl` decodes any moment by hand.
- The report opens with session-level findings: stutter count and rhythm, GPU involvement, CPU frequency cap, saturated core, memory pressure, composed-copy presentation and steady background CPU users.
- Frames that fall inside the recorder's own WPR start or ETL export are tagged as self-induced, hidden by default and excluded from statistics. Previously the largest "stutters" of a deep session were often the recorder's own.
- Closing the window no longer writes an ETL when no long frame is pending.
- PresentMon is restarted up to three times when it exits before delivering any frame.
## 1.1.0 — 2026-11-02

Initial public release.

- Portable Windows x64 recorder with a console-controlled detached worker; closing the console stops collection and completes report generation.
- PresentMon 2.6.0 frame capture aligned with system, GPU, process and scheduling-probe telemetry.
- Offline HTML timeline, per-swap-chain candidate detection, correlation details and JSON summaries.
- Optional bounded WPR/ETW recording for CPU waits/stacks, DPC/ISR, graphics/composition and file/disk/hard-fault activity.
- Automatic long-frame ETL snapshots, named-instance isolation, export interval annotations and event-loss reporting.
- Source-only Git repository, hash-verified dependency download, allowlisted release packaging and Windows CI.

Windows 11 x64 has been tested on one physical machine. Windows 10, ARM64, every game/anti-cheat combination and other hardware configurations have not been comprehensively validated.
