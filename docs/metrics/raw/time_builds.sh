#!/bin/bash
# Times clean Release builds, the machine otherwise idle. Fighter2D is left out (it doesn't build on either side, it
# wants engine work that isn't on development yet), so "the solution" is the engine and its three apps.
root=$1
wipe() { find "$root" -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} + ; }
build() { dotnet build "$root/$1" -c Release -nologo -v q -p:EnableWindowsTargeting=true >/dev/null 2>&1 || echo "FAILED $1" >&2; }
wipe
t0=$(date +%s.%N)
build Horizon.Testing/Horizon.Testing.csproj
build Horizon.Hex/Horizon.Hex.csproj
build Horizon.Tests/Horizon.Tests.csproj
t1=$(date +%s.%N)
wipe
t2=$(date +%s.%N)
build Horizon.Testing/Horizon.Testing.csproj
t3=$(date +%s.%N)
python3 -c "print('%.1f %.1f' % ($t1-$t0, $t3-$t2))"
