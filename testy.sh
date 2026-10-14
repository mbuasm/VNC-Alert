#!/usr/bin/env bash
set -euo pipefail

for projekt in \
  tests/MaszynyAlert.Core.Tests/MaszynyAlert.Core.Tests.csproj \
  tests/MaszynyAlert.Rfb.Tests/MaszynyAlert.Rfb.Tests.csproj
do
  echo "info: dotnet test $projekt"
  dotnet test "$projekt" -c Release -m:1 -nr:false
done
