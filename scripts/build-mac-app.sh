#!/usr/bin/env bash
# Builds a real, double-clickable Performance Studio.app for local use.
# Ad-hoc signed (no Apple Developer ID) — runs fine on this Mac, but
# Gatekeeper will block it on any machine it gets copied/downloaded to.
set -euo pipefail

RID="osx-$(test "$(uname -m)" = arm64 && echo arm64 || echo x64)"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PUBLISH_DIR="$ROOT/dist/app/publish-$RID"
APP="$ROOT/dist/app/Performance Studio.app"

rm -rf "$PUBLISH_DIR" "$APP"

dotnet publish "$ROOT/src/PlanViewer.App/PlanViewer.App.csproj" \
  -c Release -r "$RID" --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -o "$PUBLISH_DIR"

mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$PUBLISH_DIR/PlanViewer.App" "$APP/Contents/MacOS/PlanViewer.App"
chmod +x "$APP/Contents/MacOS/PlanViewer.App"
cp "$ROOT/src/PlanViewer.App/EDD.icns" "$APP/Contents/Resources/EDD.icns"
cp "$ROOT/src/PlanViewer.App/Info.plist" "$APP/Contents/Info.plist"

codesign --force --deep --sign - "$APP"
xattr -cr "$APP"

echo "Built: $APP"
echo "Launch with: open \"$APP\""
