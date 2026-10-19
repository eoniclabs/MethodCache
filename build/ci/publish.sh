#!/usr/bin/env bash
# Publishes a release from its tag, once the build servers have passed that commit.
# nuget.org via BWS is the default; --github selects GitHub Packages.
#
#   build/ci/publish.sh v1.2.0
#   build/ci/publish.sh v1.3.0-beta --github
#   build/ci/publish.sh v1.0.0 --nuget
#
# Run it on Megatron (or any machine with the .NET 10 SDK). GitHub Packages needs gh logged in
# with write:packages; nuget.org needs BWS and a .NET SDK 10.0.300+ publishing CLI. It packs the 7 packages
# from a fresh clone of the tag, so the
# local checkout does not matter. Running it again is safe: versions already published are skipped.
set +x
set -euo pipefail

# Do not pass publishing credentials into the clone's build steps.
publish_bws_token=${BWS_ACCESS_TOKEN:-}
unset BWS_ACCESS_TOKEN NUGET_API_KEY NUGET_SYMBOL_API_KEY

# launchd and non-login SSH sessions on macOS do not see Homebrew (gh) or the .NET SDKs
export PATH="/opt/homebrew/bin:/usr/local/share/dotnet:/usr/local/bin:$PATH"

tag=${1:?usage: build/ci/publish.sh v<major>.<minor>.<patch>[-<prerelease>] [--nuget|--github]}
target=${2:---nuget}
if [ "$#" -gt 2 ] || { [ "$target" != --github ] && [ "$target" != --nuget ]; }; then
  echo "usage: build/ci/publish.sh v<major>.<minor>.<patch>[-<prerelease>] [--nuget|--github]" >&2
  exit 2
fi
if ! [[ $tag =~ ^v([0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?)$ ]]; then
  echo "Not a release tag: $tag (expected v<major>.<minor>.<patch>, optionally with -<prerelease>)" >&2
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

if [ "$target" = --nuget ]; then
  for tool in bws jq; do
    if ! command -v "$tool" > /dev/null; then
      echo "$tool is required for NuGet publishing. Install it and ensure it is on PATH." >&2
      exit 1
    fi
  done
  secret_id=${METHODCACHE_NUGET_SECRET_ID:-}
  if [ -z "$secret_id" ] && [ -f "$HOME/.config/methodcache-publish/nuget-secret-id" ]; then
    secret_id=$(cat "$HOME/.config/methodcache-publish/nuget-secret-id")
  fi
  if ! [[ $secret_id =~ ^[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}$ ]]; then
    echo "Set METHODCACHE_NUGET_SECRET_ID to the BWS secret UUID, or store that UUID in ~/.config/methodcache-publish/nuget-secret-id." >&2
    exit 1
  fi
  publish_dotnet=${METHODCACHE_PUBLISH_DOTNET:-dotnet}
  if [ -z "${METHODCACHE_PUBLISH_DOTNET:-}" ] && [ -x "$HOME/.local/share/methodcache-publish-dotnet/dotnet" ]; then
    publish_dotnet="$HOME/.local/share/methodcache-publish-dotnet/dotnet"
  fi
  # Run outside the tagged clone so its global.json cannot select an older NuGet CLI.
  sdk_version=$(cd /; "$publish_dotnet" --version)
  if ! [[ $sdk_version =~ ^([0-9]+)\.([0-9]+)\.([0-9]+)$ ]] ||
     [ "${BASH_REMATCH[1]}" -lt 10 ] ||
     { [ "${BASH_REMATCH[1]}" -eq 10 ] && [ "${BASH_REMATCH[2]}" -eq 0 ] && [ "${BASH_REMATCH[3]}" -lt 300 ]; }; then
    echo "NuGet publishing requires .NET SDK 10.0.300 or later (NUGET_API_KEY support). Set METHODCACHE_PUBLISH_DOTNET to its dotnet executable." >&2
    exit 1
  fi
else
  scopes=$(gh api --include user 2> /dev/null | tr -d '\r' | sed -n 's/^[Xx]-[Oo][Aa]uth-[Ss]copes: //p')
  case ",${scopes// /}," in
    *,write:packages,*) ;;
    *) echo "gh cannot publish packages yet: run 'gh auth refresh -s write:packages' first." >&2; exit 1 ;;
  esac
fi

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
# Full history: Nerdbank.GitVersioning computes the version from it
git clone --quiet "https://github.com/$repo.git" "$work/src"
git -C "$work/src" checkout --quiet --detach "$sha"

export CI=true DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
# PublicRelease: a version without the -g<commit> suffix, as the tag is not checked out as a branch
# One project at a time: parallel restore and pack of the solution have failed on Megatron
# ("nuget.g.props already exists", a missing SourceGenerator output)
dotnet pack "$work/src/MethodCache.sln" --configuration Release -p:PublicRelease=true --output "$work/packages" -maxcpucount:1

packages=()
for package in MethodCache MethodCache.Redis MethodCache.SqlServer MethodCache.Serialization.MessagePack \
               MethodCache.AspNetCore MethodCache.OpenTelemetry MethodCache.HttpCaching; do
  file="$work/packages/$package.$version.nupkg"
  if [ ! -f "$file" ]; then
    echo "Missing $package $version. Does version.json at $tag say $version?" >&2
    ls "$work/packages" >&2
    exit 1
  fi
  if [ "$target" = --nuget ] && [ ! -f "$work/packages/$package.$version.snupkg" ]; then
    echo "Missing $package $version symbol package. Check IncludeSymbols and SymbolPackageFormat at $tag." >&2
    exit 1
  fi
  packages+=("$file")
done

if [ "$target" = --nuget ]; then
  (
    export BWS_ACCESS_TOKEN="${publish_bws_token:-$(cat "${BWS_TOKEN_FILE:-$HOME/.bws_token}")}"
    export NUGET_API_KEY
    NUGET_API_KEY=$(bws secret get "$secret_id" | jq -er 'select(.key == "METHODCACHE_NUGET_ORG_API_KEY") | .value | select(type == "string" and length > 0)')
    export NUGET_SYMBOL_API_KEY="$NUGET_API_KEY"
    unset BWS_ACCESS_TOKEN
    cd "$work"
    for file in "${packages[@]}"; do
      "$publish_dotnet" nuget push "$file" --source https://api.nuget.org/v3/index.json --skip-duplicate
    done
  )
  echo "Published $version to nuget.org. Verify the seven packages and their eoniclabs ownership after indexing."
  exit 0
fi

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
