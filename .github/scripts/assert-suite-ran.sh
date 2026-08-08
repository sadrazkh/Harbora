#!/usr/bin/env bash
#
# Refuses a test suite that did not actually run.
#
#   assert-suite-ran.sh <trx-path> <minimum-passing> <suite-name>
#
# `dotnet test` exits 0 for a suite that discovered nothing, and 0 for a suite whose every test
# reported an environmental skip. Both look exactly like success to a workflow that only reads the
# exit code, and both are how a green tick ends up sitting over an unexercised code path. This is
# the check that makes the tick mean something, and it is a separate file rather than a heredoc in
# each job so that the three lanes that need it cannot drift apart.
#
# It refuses four things:
#
#   1. No TRX at all. Nothing can be said about a run that left no record.
#   2. Any outcome="NotExecuted". Every skip in this repository is environmental — DockerFact and
#      PostgresFact set Skip when no daemon answers — so on a runner that has Docker a skip means
#      the environment is not what the workflow claims, not that the test is unimportant.
#   3. Any outcome="Failed". Belt and braces: the `dotnet test` step ahead of this one should
#      already have failed, and this catches the day somebody adds `|| true` or `continue-on-error`.
#   4. Fewer passing tests than the floor. A filter typo that narrows a 3,000-test suite to 3 leaves
#      a TRX with no skips, no failures and nothing in it. The floors are floors, deliberately well
#      under the real counts, so ordinary churn does not touch this file; they are a catastrophe
#      detector, not an inventory. Raise one only when it stops being catastrophic.
#
# The passing count is a count of occurrences of outcome="Passed", so a data-driven test's inner
# results are counted individually. That can only make a floor easier to clear, which is the safe
# direction for a lower bound.
set -euo pipefail

trx="${1:-}"
floor="${2:-}"
suite="${3:-}"

if [ -z "$trx" ] || [ -z "$floor" ] || [ -z "$suite" ]; then
  echo "usage: assert-suite-ran.sh <trx-path> <minimum-passing> <suite-name>" >&2
  exit 2
fi

case "$floor" in
  ''|*[!0-9]*)
    echo "assert-suite-ran.sh: the minimum-passing argument must be a number, got '$floor'." >&2
    exit 2 ;;
esac

# A floor of 0 disables the only check that catches a TRX which is well-formed and empty, turning
# this script into an expensive way of agreeing with `dotnet test`. If a suite genuinely has no
# tests it should not have a guard; if it has tests, it has a floor above zero.
if [ "$floor" -eq 0 ]; then
  echo "assert-suite-ran.sh: a minimum-passing floor of 0 would accept a TRX with no passing tests in it, which is the thing this script exists to refuse. Give a real floor." >&2
  exit 2
fi

# GitHub renders ::error:: as an annotation on the job. Outside Actions it is just a prefixed line,
# which is why this script is runnable by hand:
#   bash .github/scripts/assert-suite-ran.sh ./trx/panel.trx 3000 'Harbora.Tests'
fail() { echo "::error::$suite — $1"; exit 1; }

if [ ! -f "$trx" ]; then
  fail "no TRX was produced at $trx, so there is no evidence the suite ran at all."
fi

if [ ! -s "$trx" ]; then
  fail "the TRX at $trx is empty, so there is no evidence the suite ran at all."
fi

if grep -q 'outcome="NotExecuted"' "$trx"; then
  # The TRX puts testName and outcome on the same element, so one grep gets both.
  skipped_names="$(grep -o '<UnitTestResult[^>]*outcome="NotExecuted"[^>]*>' "$trx" \
    | grep -o 'testName="[^"]*"' | sed 's/^testName="/  - /; s/"$//' || true)"
  skipped_count="$(printf '%s\n' "$skipped_names" | grep -c '^  - ' || true)"
  skipped_count="$(printf '%s' "${skipped_count:-0}" | tr -d '[:space:]')"

  echo "::error::$suite — $skipped_count test(s) were skipped on a runner that is supposed to be able to run them."
  printf '%s\n' "$skipped_names" | head -20
  [ "$skipped_count" -gt 20 ] && echo "  … and $((skipped_count - 20)) more."

  # Not strictly per-test — the TRX interleaves messages — but every environmental skip in this
  # repository records why, and the distinct reasons are what an operator needs to read.
  echo "Reason(s) recorded in this TRX:"
  grep -o '<Message>[^<]*</Message>' "$trx" \
    | sed 's|<Message>|  |; s|</Message>||' | sort -u | head -3 || true
  exit 1
fi

if grep -q 'outcome="Failed"' "$trx"; then
  echo "::error::$suite — the TRX records failing tests."
  grep -o '<UnitTestResult[^>]*outcome="Failed"[^>]*>' "$trx" \
    | grep -o 'testName="[^"]*"' | sed 's/^testName="/  - /; s/"$//' || true
  exit 1
fi

# -o then wc, not `grep -c`: grep -c counts matching *lines*, and a TRX is not guaranteed to put one
# result per line. `|| true` because pipefail would otherwise kill the script on a TRX with no
# passing test at all — which is a case this script exists to report, not to die on.
passed="$(grep -o 'outcome="Passed"' "$trx" | wc -l || true)"
passed="$(printf '%s' "${passed:-0}" | tr -d '[:space:]')"

if [ "$passed" -lt "$floor" ]; then
  fail "the TRX records $passed passing tests, fewer than the floor of $floor. A suite that discovers almost nothing is a suite that did not run."
fi

echo "ok  $suite — $passed passing, none skipped, none failed."
