#!/usr/bin/env bash
# uzycie: blokada.sh < json-haka-PreToolUse
set -uo pipefail

wejscie="$(cat)"
polecenie="$(printf '%s' "$wejscie" | tr '\r\n\t' '   ' \
  | grep -oE '"command"[[:space:]]*:[[:space:]]*"(\\.|[^"\\])*"' | head -n 1 \
  | sed -E 's/^"command"[[:space:]]*:[[:space:]]*"//; s/"$//; s/\\[nrt]/;/g; s/\\"/"/g')"
[ -n "$polecenie" ] || exit 0

odmow() {
  echo "blad: $1 - wykonuje wylacznie wlasciciel" >&2
  exit 2
}

while IFS= read -r odcinek; do
  if printf '%s' "$odcinek" | grep -qE '(^|[[:space:]])git[[:space:]].*push'; then
    printf '%s' "$odcinek" | grep -qE '[[:space:]](--force[^[:space:]]*|-[a-zA-Z]*f([[:space:]]|$))|[[:space:]]\+[^[:space:]]' \
      && odmow "force push zabroniony"
    printf '%s' "$odcinek" | grep -qE '[[:space:]]([^[:space:]]*:)?(refs/heads/)?main([[:space:]]|$)' \
      && odmow "push na main zabroniony"
  fi
  printf '%s' "$odcinek" | grep -qE '(^|[[:space:]])gh[[:space:]]+pr[[:space:]]+merge' && odmow "scalanie PR zabronione"
  printf '%s' "$odcinek" | grep -qE '(^|[[:space:]])gh[[:space:]]+pr[[:space:]]+ready' && odmow "oznaczanie PR jako gotowy do recenzji zabronione"
  printf '%s' "$odcinek" | grep -qE '(^|[[:space:]])gh[[:space:]]+api[[:space:]].*(/merge([[:space:]]|$)|markPullRequestReadyForReview|mergePullRequest)' \
    && odmow "scalanie lub oznaczanie PR przez API zabronione"
done < <(printf '%s\n' "$polecenie" | tr ';&|' '\n\n\n')
exit 0
