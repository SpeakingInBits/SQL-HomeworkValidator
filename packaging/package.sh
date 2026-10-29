#!/usr/bin/env bash
# Builds the desktop app as a self-contained single file for each runtime and packages it for a release.
#
#   VERSION=1.2.3 packaging/package.sh <rid>...
#
# Run from the repository root. Writes dist/HomeworkValidator-<version>-<rid>.zip, or .tar.gz for Linux, which keeps
# the executable bit. macOS builds are .app bundles with an ad-hoc signature, which Apple silicon needs before it runs
# anything. They aren't notarized, so Gatekeeper still warns the first time (see release-notes.md).
set -euo pipefail

version=${VERSION:?set VERSION, e.g. VERSION=1.2.3}
mkdir -p dist
dist=$(cd dist && pwd)

for rid in "$@"; do
  out="out/$rid"
  rm -rf "$out"

  # On macOS the native libraries sit next to the executable inside the bundle, so the bundle's signature covers them.
  # Elsewhere they go inside the single file.
  bundle_native=true
  [[ $rid == osx-* ]] && bundle_native=false

  # Not trimmed: YamlDotNet and SqlClient use reflection that trimming breaks (the content fails to load).
  dotnet publish src/HomeworkValidator.App --configuration Release --runtime "$rid" --self-contained --output "$out/publish" \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=$bundle_native -p:EnableCompressionInSingleFile=true \
    -p:DebugType=none -p:Version="$version"

  # Some native packages ship their own debug symbols, which students don't need.
  find "$out/publish" -name '*.pdb' -delete

  name="HomeworkValidator-$version-$rid"
  case "$rid" in
    win-*)
      mv "$out/publish" "$out/HomeworkValidator"
      mv "$out/HomeworkValidator/HomeworkValidator.App.exe" "$out/HomeworkValidator/HomeworkValidator.exe"
      pwsh -NoProfile -Command "Compress-Archive -Path '$out/HomeworkValidator' -DestinationPath 'dist/$name.zip' -Force"
      ;;
    osx-*)
      app="$out/SQL Homework Validator.app"
      mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
      mv "$out/publish/content" "$app/Contents/Resources/"
      mv "$out/publish/"* "$app/Contents/MacOS/"
      mv "$app/Contents/MacOS/HomeworkValidator.App" "$app/Contents/MacOS/HomeworkValidator"
      sed "s/{{VERSION}}/$version/g" packaging/Info.plist > "$app/Contents/Info.plist"
      codesign --force --deep --sign - "$app"
      codesign --verify --deep --strict "$app"
      ditto -c -k --keepParent "$app" "$dist/$name.zip"
      ;;
    linux-*)
      mv "$out/publish" "$out/HomeworkValidator"
      mv "$out/HomeworkValidator/HomeworkValidator.App" "$out/HomeworkValidator/HomeworkValidator"
      tar -czf "$dist/$name.tar.gz" -C "$out" HomeworkValidator
      ;;
    *)
      echo "Don't know how to package $rid." >&2
      exit 1
      ;;
  esac
done
