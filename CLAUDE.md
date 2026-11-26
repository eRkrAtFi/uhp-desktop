# CLAUDE.md

Project-specific instructions for Claude Code sessions working in this
repository (`riscv-emu/`, `QemuManager/`, `vision/`, `docs/`).

## Git workflow

- Never commit or push directly to `main`.
- Every phase or larger change gets its own branch, named `phase/<name>`
  or `fix/<name>`. Commits on it can be frequent; push it anytime as a
  backup - that does not need to wait for the work to be finished.
- When the change is done and green locally (`QemuManager/finish.ps1`
  for QemuManager/UHP.Core; `make test-all` and `make compare` for
  riscv-emu, as applicable), open a pull request into `main` with a
  summary of the change.
- **"Approval" means the repo owner clicks Merge on the pull request
  themselves, once CI is green - not an approving review.** (A solo
  repo has no second person who could approve the author's own PR.)
  Claude never merges a pull request itself, regardless of how green
  CI is - it opens the PR, confirms CI is passing, and then waits; the
  repo owner merges it whenever they choose to.
- Squash-merge into `main`, then tag and create the release - never tag
  a branch directly, and never tag/release before the merge has
  actually landed on `main`.
- `main` is protected on GitHub (see repo Settings -> Branches): changes
  only land through a pull request with a passing `build-and-test`
  check; force-pushes and branch deletion are disabled. No approving
  review count is required (there is no second reviewer to require it
  from) - the merge button itself is the approval.
