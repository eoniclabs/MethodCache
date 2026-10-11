#!/usr/bin/env bash
# Publishes a release to GitHub Packages from its tag, once the build servers have passed that commit.
# Never publishes to nuget.org.
#
#   build/ci/publish.sh v1.2.0
#
# Run it on Megatron (or any machine with the .NET 8, 9 and 10 SDKs and gh logged in with the write:packages
# scope: `gh auth refresh -s write:packages`). It packs the 7 packages from a fresh clone of the tag, so the
# local checkout does not matter. Running it again is safe: versions already published are skipped.
set -euo pipefail

# launchd and non-login SSH sessions on macOS do not see Homebrew (gh) or the .NET SDKs
export PATH="/opt/homebrew/bin:/usr/local/share/dotnet:/usr/local/bin:$PATH"

tag=${1:?usage: build/ci/publish.sh v<major>.<minor>.<patch>}
if ! [[ $tag =~ ^v([0-9]+\.[0-9]+\.[0-9]+)$ ]]; then
  echo "Not a release tag: $tag (expected v<major>.<minor>.<patch>)" >&2
  exit 2
fi
version=${BASH_REMATCH[1]}
repo="${METHODCACHE_CI_REPO:-eoniclabs/MethodCache}"
owner=${repo%%/*}

sha=$(gh api "repos/$repo/commits/$tag" --jq .sha)
state=$(gh api "repos/$repo/commits/$sha/status" --jq '[.statuses[] | select(.context == "MethodCache CI")][0].state // "missing"')
if [ "$state" != success ]; then
  echo "The MethodCache CI status of $tag (${sha:0:12}) is '$state'. Publish only a commit the build servers passed." >&2
  exit 1
fi

scopes=$(gh api --include user 2> /dev/null | tr -d '\r' | sed -n 's/^[Xx]-[Oo][Aa]uth-[Ss]copes: //p')
case ",${scopes// /}," in
  *,write:packages,*) ;;
  *) echo "gh cannot publish packages yet: run 'gh auth refresh -s write:packages' first." >&2; exit 1 ;;
esac

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
# Full history: Nerdbank.GitVersioning computes the version from it
git clone --quiet "https://github.com/$repo.git" "$work/src"
git -C "$work/src" checkout --quiet --detach "$sha"

export CI=true DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
# PublicRelease: a version without the -g<commit> suffix, as the tag is not checked out as a branch
dotnet pack "$work/src/MethodCache.sln" --configuration Release -p:PublicRelease=true --output "$work/packages"

packages=()
for package in MethodCache MethodCache.Redis MethodCache.SqlServer MethodCache.Serialization.MessagePack \
               MethodCache.AspNetCore MethodCache.OpenTelemetry MethodCache.HttpCaching; do
  file="$work/packages/$package.$version.nupkg"
  if [ ! -f "$file" ]; then
    echo "Missing $package $version. Does version.json at $tag say $version?" >&2
    ls "$work/packages" >&2
    exit 1
  fi
  packages+=("$file")
done

token=$(gh auth token)
for file in "${packages[@]}"; do
  dotnet nuget push "$file" --api-key "$token" --source "https://nuget.pkg.github.com/$owner/index.json" \
    --skip-duplicate
done

for file in "${packages[@]}"; do
  package=$(basename "$file" ".$version.nupkg")
  gh api --method PATCH -H "Accept: application/vnd.github+json" "/orgs/$owner/packages/nuget/$package" \
    -f visibility=public > /dev/null \
    || echo "Could not make $package public (it may be public already)"
done

echo "Published $version: https://github.com/$repo/packages"
