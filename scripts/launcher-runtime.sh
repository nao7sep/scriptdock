#!/usr/bin/env bash
# Sourced by the macOS launchers. Only an app's explicit registration grants process ownership;
# command lines, executable names and dotnet's parent process never do.

claim_launcher_runtime() {
  local token="$1" repo_dir="$2"
  local owner_file="$repo_dir/.git/launcher-runtime-owner"
  local temporary_file="${owner_file}.$$.$RANDOM"
  printf '%s\n' "$token" > "$temporary_file"
  mv -f "$temporary_file" "$owner_file"
}

is_launcher_runtime_owner() {
  local token="$1" repo_dir="$2"
  local owner_file="$repo_dir/.git/launcher-runtime-owner"
  [[ -f "$owner_file" && "$(tr -d '\r\n' < "$owner_file")" == "$token" ]]
}

release_launcher_runtime() {
  local token="$1" repo_dir="$2"
  if is_launcher_runtime_owner "$token" "$repo_dir"; then
    rm -f "$repo_dir/.git/launcher-runtime-owner"
  fi
}

registered_runtime_matches() {
  local pid="$1" started="$2" actual_start actual_seconds
  [[ "$pid" =~ ^[1-9][0-9]*$ && "$started" =~ ^[0-9]+$ ]] || return 1
  actual_start="$(LC_ALL=C ps -p "$pid" -o lstart= 2>/dev/null)" || return 1
  [[ -n "$actual_start" ]] || return 1
  actual_seconds="$(LC_ALL=C date -j -f '%a %b %e %T %Y' "$actual_start" '+%s' 2>/dev/null)" || return 1
  [[ "$actual_seconds" == "$started" ]]
}

read_runtime_registration() {
  local registration_file="$1"
  [[ -f "$registration_file" ]] || return 1
  {
    IFS= read -r REGISTERED_TOKEN &&
    IFS= read -r REGISTERED_PID &&
    IFS= read -r REGISTERED_START
  } < "$registration_file"
}

stop_owned_runtime() {
  local label="$1" repo_dir="$2" token="$3" require_current_registration="${4:-false}"
  local REGISTERED_TOKEN REGISTERED_PID REGISTERED_START deadline
  is_launcher_runtime_owner "$token" "$repo_dir" || return 0
  read_runtime_registration "$repo_dir/.git/launcher-runtime-process" || return 0
  if [[ "$require_current_registration" == true && "$REGISTERED_TOKEN" != "$token" ]]; then
    return 0
  fi
  registered_runtime_matches "$REGISTERED_PID" "$REGISTERED_START" || return 0
  echo "Stopping the existing $label runtime (pid $REGISTERED_PID)."
  is_launcher_runtime_owner "$token" "$repo_dir" || return 0
  registered_runtime_matches "$REGISTERED_PID" "$REGISTERED_START" || return 0
  kill -TERM "$REGISTERED_PID" 2>/dev/null || true
  deadline=$((SECONDS + 5))
  while (( SECONDS < deadline )); do
    registered_runtime_matches "$REGISTERED_PID" "$REGISTERED_START" || return 0
    sleep 0.1
  done
  is_launcher_runtime_owner "$token" "$repo_dir" || return 0
  registered_runtime_matches "$REGISTERED_PID" "$REGISTERED_START" || return 0
  kill -KILL "$REGISTERED_PID" 2>/dev/null || true
}

wait_for_owned_runtime() {
  local label="$1" repo_dir="$2" token="$3" timeout_seconds="$4"
  local deadline REGISTERED_TOKEN REGISTERED_PID REGISTERED_START
  deadline=$((SECONDS + timeout_seconds))
  while (( SECONDS < deadline )); do
    is_launcher_runtime_owner "$token" "$repo_dir" || return 1
    if read_runtime_registration "$repo_dir/.git/launcher-runtime-process" && [[ "$REGISTERED_TOKEN" == "$token" ]]; then
      # PID zero acknowledges activation of an independently started instance without owning it.
      if [[ "$REGISTERED_PID" == 0 ]] || registered_runtime_matches "$REGISTERED_PID" "$REGISTERED_START"; then
        return 0
      fi
    fi
    sleep 0.1
  done
  echo "$label did not start within ${timeout_seconds}s." >&2
  return 1
}
