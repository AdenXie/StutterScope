# Contributing

The first release targets Windows 11 x64. Compatibility reports from other hardware and driver combinations are welcome; please distinguish tested results from assumptions.

1. Download the pinned dependency with `tools/fetch-presentmon.ps1`.
2. Build with `build.ps1`, then run `StutterScope.exe --self-test` or `tests/smoke.ps1 -Quick`.
3. For collection changes, run the ordinary-permission smoke test. Run the administrator ETW test only on a local test machine where its overhead is acceptable.
4. Use `Create-Release.ps1` to build an allowlisted portable package. Do not add recordings, ETLs, dumps, generated reports or test output to commits or release assets.

Bug reports should include the OS version, CPU/GPU and driver versions, capture mode, whether frame counts increased, and a redacted error/status excerpt. Full ETL files can reveal local file paths and process activity; do not attach them to public issues without reviewing their contents.

The HTML report identifies correlations and candidate stalls. Avoid changes that label a driver or process as a proven cause based only on a utilization spike. Preserve missing measurements as missing, and keep WPR actions scoped to the recorder's unique instance name.
