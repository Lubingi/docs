#!/usr/bin/env bash
# Builds installer/Output/LiveSubtitlesSetup-<version>.exe with NSIS. Works on Linux (apt install nsis) or on
# Windows with Git Bash (winget install NSIS.NSIS). Needs the .NET 10 SDK.
set -euo pipefail
cd "$(dirname "$0")/.."
VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props)
[ "${SKIP_TESTS:-}" = 1 ] || dotnet test tests/LiveSubtitles.Core.Tests -c Release
rm -rf artifacts/publish
dotnet publish src/LiveSubtitles.App -c Release -r win-x64 --self-contained true \
  -p:PublishReadyToRun=true -p:DebugType=none -o artifacts/publish
rm -f artifacts/publish/*.lib
for m in silero_vad.onnx campplus_voxceleb_16k.onnx; do
  [ -f "artifacts/publish/models/$m" ] || { echo "model $m missing from publish output"; exit 1; }
done
mkdir -p installer/Output
makensis -V2 -DVERSION="$VERSION" installer/LiveSubtitles.nsi
ls -la installer/Output/
