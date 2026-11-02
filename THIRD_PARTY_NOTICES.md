# Third-party components

## PresentMon

- Upstream: https://github.com/GameTechDev/PresentMon
- Pinned version: [v2.6.0](https://github.com/GameTechDev/PresentMon/releases/tag/v2.6.0)
- Binary: `PresentMon-2.6.0-x64.exe`, downloaded from the upstream GitHub release.
- SHA-256: `b2a706bc6ad475749e3b7e3409263aa1e6906d45bdcf993f6dbc0f660188f1af`
- License: MIT; the upstream copyright and permission notice is preserved in [tools/PresentMon-LICENSE.txt](tools/PresentMon-LICENSE.txt).

The portable release redistributes the unmodified console binary and license. The source repository contains a hash-verified download script instead of the binary.

`tests/presentmon-official-v1.csv` is the upstream `Tests/Gold/test_case_0_v1.csv` from the same tag, distributed under the same license.

## Windows components

WPR, PDH, .NET Framework and Windows system DLLs are used from the destination operating system. They are not redistributed by this project. `DeepTrace.wprp` selects provider GUIDs and keywords from the Windows 11 built-in CPU, GPU, desktop composition, disk and file I/O profiles and sets bounded buffers.
