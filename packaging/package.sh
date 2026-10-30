#!/usr/bin/env bash
# Builds the desktop app as a self-contained single file for each runtime and packages it with Velopack, so installed
# copies can update themselves from the GitHub releases.
#
#   VERSION=1.2.3 packaging/package.sh <rid>...
#
# Run from the repository root, with vpk (dotnet tool install -g vpk) on the PATH at the same version as the Velopack
# package in HomeworkValidator.App.csproj. Each runtime is its own Velopack channel, named after the runtime, so an
# installed app only ever updates to its own platform's package. Writes to dist/:
#
#   HomeworkValidator-<rid>-Setup.exe       Windows installer (installs for the current user, no admin needed)
#   HomeworkValidator-<rid>-Portable.zip    Windows folder or macOS .app that runs from anywhere and still updates
#   HomeworkValidator-<rid>.AppImage        Linux
#   HomeworkValidator-<version>-<rid>-full.nupkg, releases.<rid>.json   what installed apps download to update
#
# Only the macOS packages are built on a Mac. They get an ad-hoc signature, which Apple silicon needs before it runs
# anything, but aren't notarized, so Gatekeeper still warns the first time (see release-notes.md).
set -euo pipefail

version=${VERSION:?set VERSION, e.g. VERSION=1.2.3}
mkdir -p dist
dist=$(cd dist && pwd)

pack() {
  # No delta packages: each update downloads the whole app, which keeps the release simple.
  vpk pack --packId HomeworkValidator --packTitle "SQL Homework Validator" --packAuthors SpeakingInBits \
    --packVersion "$version" --channel "$rid" --runtime "$rid" --delta None --outputDir "$dist" "$@"
}

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

  case "$rid" in
    win-*)
      mv "$out/publish" "$out/HomeworkValidator"
      mv "$out/HomeworkValidator/HomeworkValidator.App.exe" "$out/HomeworkValidator/HomeworkValidator.exe"
      pack --packDir "$out/HomeworkValidator" --mainExe HomeworkValidator.exe
      ;;
    osx-*)
      app="$out/SQL Homework Validator.app"
      mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
      mv "$out/publish/content" "$app/Contents/Resources/"
      mv "$out/publish/"* "$app/Contents/MacOS/"
      mv "$app/Contents/MacOS/HomeworkValidator.App" "$app/Contents/MacOS/HomeworkValidator"
      sed "s/{{VERSION}}/$version/g" packaging/Info.plist > "$app/Contents/Info.plist"
      # vpk adds its updater to the bundle, then signs the whole bundle; "-" is an ad-hoc signature.
      # No .pkg installer: without a Developer ID it's blocked just like the app, and the zipped .app is simpler.
      pack --packDir "$app" --mainExe HomeworkValidator --signAppIdentity - --noInst
      # Check the signature of the bundle students actually get.
      ditto -x -k "$dist/HomeworkValidator-$rid-Portable.zip" "$out/check"
      codesign --verify --deep --strict "$out/check/SQL Homework Validator.app"
      ;;
    linux-*)
      mv "$out/publish" "$out/HomeworkValidator"
      mv "$out/HomeworkValidator/HomeworkValidator.App" "$out/HomeworkValidator/HomeworkValidator"
      pack --packDir "$out/HomeworkValidator" --mainExe HomeworkValidator
      ;;
    *)
      echo "Don't know how to package $rid." >&2
      exit 1
      ;;
  esac
done
