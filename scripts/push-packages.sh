#!/usr/bin/env bash
set -euo pipefail

registry="${1:?Specify nuget or github}"
: "${PUBLISH_API_KEY:?Publication credentials are required}"
case "$registry" in
  nuget) source='https://api.nuget.org/v3/index.json' ;;
  github)
    : "${GITHUB_REPOSITORY_OWNER:?Repository owner is required}"
    source="https://nuget.pkg.github.com/${GITHUB_REPOSITORY_OWNER,,}/index.json"
    ;;
  *) echo 'Unknown registry. Use nuget or github.' >&2; exit 1 ;;
esac

while IFS= read -r package; do
  dotnet nuget push "artifacts/publish/$package" \
    --source "$source" --api-key "$PUBLISH_API_KEY" --skip-duplicate --no-symbols
  if [[ "$registry" == 'nuget' ]]; then
    # Push symbols explicitly so a retry can repair a partially published pair.
    dotnet nuget push "artifacts/publish/${package%.nupkg}.snupkg" \
      --source "$source" --api-key "$PUBLISH_API_KEY" --skip-duplicate
  fi
done < artifacts/publish/packages.txt
