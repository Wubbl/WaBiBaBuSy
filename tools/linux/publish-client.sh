#!/usr/bin/env bash
# Build a self-contained Linux client (no .NET install needed on the target) and pack it.
#   tools/linux/publish-client.sh [linux-x64|linux-arm64]
set -euo pipefail
RID="${1:-linux-x64}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
OUT="$ROOT/publish-linux/wabibabusy-client-$RID"  # not publish/: the Windows installer packs that whole folder

rm -rf "$OUT"
dotnet publish "$ROOT/WaBiBaBuSy.Client.Linux/WaBiBaBuSy.Client.Linux.csproj" \
    -c Release -r "$RID" --self-contained -o "$OUT"
tar -czf "$OUT.tar.gz" -C "$ROOT/publish-linux" "wabibabusy-client-$RID"
echo "Done: $OUT.tar.gz"
echo "On each Linux machine: tar xzf $(basename "$OUT").tar.gz && bash wabibabusy-client-$RID/packaging/install.sh"
