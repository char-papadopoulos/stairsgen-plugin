#!/bin/bash
# Builds the Rhino plug-in and the Grasshopper components and packages them together:
#   dist/Stairs-<version>.zip                 Stairs.rhp + StairsGen.gha, INSTALL.txt, LICENSE.txt
#   dist/stairsgen-<version>-rh8-any.yak      the same two files as one Package Manager package
# The version comes from the Rhino plug-in's project file unless given: tools/package.sh 1.5.0
set -e
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
VERSION="${1:-$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/src/StairsPlugin/StairsPlugin.csproj")}"
dotnet build "$ROOT/src/StairsPlugin/StairsPlugin.csproj" -c Release -p:Version="$VERSION" -nologo -v q
dotnet build "$ROOT/src/StairsGH/StairsGH.csproj" -c Release -p:Version="$VERSION" -nologo -v q

DIST="$ROOT/dist"
NAME="Stairs-$VERSION"
rm -rf "$DIST/$NAME" "$DIST/package" "$DIST/$NAME.zip"
mkdir -p "$DIST/$NAME" "$DIST/package"
for dir in "$DIST/$NAME" "$DIST/package"; do
    cp "$ROOT/src/StairsPlugin/bin/Release/StairsPlugin.dll" "$dir/Stairs.rhp"
    cp "$ROOT/src/StairsGH/bin/Release/StairsGen.gha" "$dir/StairsGen.gha"
    cp "$ROOT/LICENSE" "$dir/LICENSE.txt"
done
sed "s/VERSION/$VERSION/" "$ROOT/packaging/INSTALL.txt" > "$DIST/$NAME/INSTALL.txt"
(cd "$DIST" && zip -qr "$NAME.zip" "$NAME")
echo "$DIST/$NAME.zip"

# the Package Manager package, when Rhino's yak tool is installed
sed "s/VERSION/$VERSION/" "$ROOT/packaging/manifest.yml" > "$DIST/package/manifest.yml"
cp "$ROOT/src/StairsPlugin/assets/icons/stairs.png" "$DIST/package/icon.png"
for v in 9 8; do
    YAK="/Applications/Rhino $v.app/Contents/Resources/bin/yak"
    [ -x "$YAK" ] && break
done
if [ -x "$YAK" ]; then
    (cd "$DIST/package" && "$YAK" build --platform any >/dev/null && mv -f ./*.yak "$DIST/")
    ls "$DIST"/*.yak
else
    echo "No yak tool found; the .yak package was not built." >&2
fi
