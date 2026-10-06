#!/usr/bin/env bash
# Works out the next release from the last vX.Y.Z tag and the pull requests merged since then:
#
#   no release tag yet                 -> 0.1.0
#   any feature/* branch merged since  -> next minor   (0.x.0)
#   otherwise (fix/* etc.)             -> next patch   (0.0.x)
#
# Major versions are never bumped automatically.
#
# Usage: next-release.sh [notes-file]
#   Prints the version on stdout. With a notes file, also writes the release notes (Markdown) there.
# Needs: git history incl. tags, gh CLI (GH_TOKEN), GITHUB_REPOSITORY=owner/repo.
set -euo pipefail

notes_file="${1:-}"
repo="${GITHUB_REPOSITORY:?GITHUB_REPOSITORY must be set (owner/repo)}"

last_tag=$(git describe --tags --abbrev=0 --match 'v[0-9]*.[0-9]*.[0-9]*' 2>/dev/null || true)
if [[ -n "$last_tag" ]]; then
  new_commits=$(git rev-list "$last_tag..HEAD")
else
  new_commits=$(git rev-list HEAD)
fi

# Merged pull requests whose merge commit is new in this release, as "number<TAB>branch<TAB>title".
# Matching on the merge commit works for merge, squash and rebase merges alike.
prs=""
while IFS=$'\t' read -r sha number branch title; do
  if [[ -n "$sha" ]] && grep -qx "$sha" <<< "$new_commits"; then
    prs+="$number"$'\t'"$branch"$'\t'"$title"$'\n'
  fi
done < <(gh pr list --repo "$repo" --state merged --limit 1000 \
           --json number,headRefName,title,mergeCommit \
           --jq '.[] | [.mergeCommit.oid, (.number | tostring), .headRefName, .title] | @tsv')

if [[ -z "$last_tag" ]]; then
  version="0.1.0"
else
  IFS=. read -r major minor patch <<< "${last_tag#v}"
  if cut -f2 <<< "$prs" | grep -q '^feature/'; then
    version="$major.$((minor + 1)).0"
  else
    version="$major.$minor.$((patch + 1))"
  fi
fi

if [[ -n "$notes_file" ]]; then
  # "- Title (#12)" for every PR whose branch starts with the given prefix.
  list_prs() { awk -F'\t' -v prefix="$1" 'index($2, prefix) == 1 { printf "- %s (#%s)\n", $3, $1 }' <<< "$prs"; }
  features=$(list_prs "feature/")
  fixes=$(list_prs "fix/")
  {
    if [[ -n "$features" ]]; then printf '### New features\n%s\n\n' "$features"; fi
    if [[ -n "$fixes" ]]; then printf '### Fixes\n%s\n\n' "$fixes"; fi
    if [[ -z "$features$fixes" ]]; then printf 'Maintenance release.\n\n'; fi
  } > "$notes_file"
fi

echo "$version"
