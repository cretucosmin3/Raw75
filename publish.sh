#!/usr/bin/env bash
# Self-contained Release publishes for linux-x64 and win-x64.
# Output: <repo>/publish/linux-x64  and  <repo>/publish/win-x64
#
# Performance: ReadyToRun composite (fast startup), Dynamic PGO, Speed
# optimizations. No trim / AOT / single-file — those break Skia, Silk.NET,
# GLFW, and LibRaw, or add extract cost on every launch.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$ROOT"

PROJECT="$ROOT/src/Raw75/Raw75.csproj"
PUBLISH="$ROOT/publish"
if [[ -d "$ROOT/external/Blossom/src/Blossom" ]]; then
  DEFAULT_BLOSSOM="$ROOT/external/Blossom"
else
  DEFAULT_BLOSSOM="$(cd "$ROOT/../Blossom" 2>/dev/null && pwd || echo "")"
fi
BLOSSOM_ROOT="${BLOSSOM_ROOT:-$DEFAULT_BLOSSOM}"

if [[ ! -f "$PROJECT" ]]; then
  echo "error: missing $PROJECT" >&2
  exit 1
fi
if [[ ! -d "$BLOSSOM_ROOT/src/Blossom" ]]; then
  echo "error: Blossom not found at $BLOSSOM_ROOT (set BLOSSOM_ROOT)" >&2
  exit 1
fi

# Shared publish properties. Workstation GC stays on (WinExe default) — better
# for a desktop UI than server GC.
PUBLISH_PROPS=(
  -c Release
  --self-contained true
  -p:BlossomRoot="$BLOSSOM_ROOT/"
  -p:PublishReadyToRun=true
  -p:PublishReadyToRunComposite=true
  -p:TieredCompilation=true
  -p:TieredPGO=true
  -p:OptimizationPreference=Speed
  -p:DebuggerSupport=false
  -p:MetadataUpdaterSupport=false
  -p:DebugType=none
  -p:DebugSymbols=false
  -p:PublishSingleFile=false
  -p:IncludeNativeLibrariesForSelfExtract=false
  -p:SatelliteResourceLanguages=en
  -p:UseAppHost=true
)

publish_rid() {
  local rid="$1"
  local dest="$PUBLISH/$rid"
  echo "=== Publishing $rid (self-contained, R2R composite) → $dest ==="
  rm -rf "$dest"
  mkdir -p "$dest"

  if ! dotnet publish "$PROJECT" \
      -r "$rid" \
      -o "$dest" \
      "${PUBLISH_PROPS[@]}"; then
    echo "Composite R2R failed for $rid; retrying without composite." >&2
    rm -rf "$dest"
    mkdir -p "$dest"
    local fallback=()
    local p
    for p in "${PUBLISH_PROPS[@]}"; do
      [[ "$p" == -p:PublishReadyToRunComposite=* ]] && continue
      fallback+=("$p")
    done
    dotnet publish "$PROJECT" \
      -r "$rid" \
      -o "$dest" \
      "${fallback[@]}"
  fi
}

stage_linux() {
  local out="$PUBLISH/linux-x64"
  local glfw="$BLOSSOM_ROOT/glfw/libglfw.so.3.3"
  if [[ -f "$glfw" ]]; then
    rm -f "$out/libglfw.so.3"
    cp -f "$glfw" "$out/libglfw.so.3"
    chmod +x "$out/libglfw.so.3"
  fi
  local n="$out/runtimes/linux-x64/native"
  local raw=""
  if [[ -f "$out/libraw_r.so.23" ]]; then
    raw="$out/libraw_r.so.23"
  elif [[ -f "$n/libraw_r.so.23" ]]; then
    raw="$n/libraw_r.so.23"
    cp -f "$raw" "$out/libraw_r.so.23"
  fi
  if [[ -n "$raw" ]]; then
    cp -f "$raw" "$out/libraw.so"
  fi
  local f
  for f in libjpeg.so.8 libgomp.so.1 liblcms2.so libSkiaSharp.so; do
    [[ -f "$n/$f" ]] && cp -f "$n/$f" "$out/"
  done
  if [[ -x "$out/Raw75" ]]; then
    chmod +x "$out/Raw75"
  else
    echo "error: linux publish missing Raw75" >&2
    exit 1
  fi
}

stage_windows() {
  local out="$PUBLISH/win-x64"
  local glfw="$BLOSSOM_ROOT/glfw/glfw3-x64.dll"
  if [[ -f "$glfw" ]]; then
    cp -f "$glfw" "$out/glfw3.dll"
  fi
  local n="$out/runtimes/win-x64/native"
  local raw=""
  if [[ -f "$out/raw_r.dll" ]]; then
    raw="$out/raw_r.dll"
  elif [[ -f "$n/raw_r.dll" ]]; then
    raw="$n/raw_r.dll"
    cp -f "$raw" "$out/raw_r.dll"
  fi
  if [[ -n "$raw" ]]; then
    cp -f "$raw" "$out/libraw.dll"
  fi
  local f
  for f in jpeg8.dll lcms2.dll zlib1.dll libSkiaSharp.dll; do
    [[ -f "$n/$f" ]] && cp -f "$n/$f" "$out/"
  done
  if [[ ! -f "$out/Raw75.exe" ]]; then
    echo "error: windows publish missing Raw75.exe" >&2
    exit 1
  fi
}

echo "Repo     $ROOT"
echo "Blossom  $BLOSSOM_ROOT"
echo "Output   $PUBLISH/{linux-x64,win-x64}"
mkdir -p "$PUBLISH"

publish_rid linux-x64
stage_linux

publish_rid win-x64
stage_windows

echo
echo "=== Done ==="
echo "  Linux:   $PUBLISH/linux-x64/Raw75"
echo "  Windows: $PUBLISH/win-x64/Raw75.exe"
du -sh "$PUBLISH/linux-x64" "$PUBLISH/win-x64" 2>/dev/null || true
