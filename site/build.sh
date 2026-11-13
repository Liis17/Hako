#!/bin/sh
# Builds a static copy of the site into dist/ with cache-busted CSS and JS.
set -eu

root=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
src="$root/src"
dist="$root/dist"

hash_of() {
  if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1"; else shasum -a 256 "$1"; fi | cut -c1-10
}

rm -rf "$dist"
mkdir -p "$dist/assets"
cp "$src"/assets/* "$dist/assets/"

css=$(hash_of "$src/styles.css")
js=$(hash_of "$src/script.js")
cp "$src/styles.css" "$dist/styles.$css.css"
cp "$src/script.js" "$dist/script.$js.js"
sed -e "s|\./styles\.css|./styles.$css.css|" -e "s|\./script\.js|./script.$js.js|" "$src/index.html" > "$dist/index.html"

grep -q "styles.$css.css" "$dist/index.html"
grep -q "script.$js.js" "$dist/index.html"
echo "Built $dist"
