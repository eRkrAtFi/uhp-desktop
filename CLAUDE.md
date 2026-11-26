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
  summary of the change, and wait for approval before merging - never
  merge a pull request without it.
- After approval, squash-merge into `main`. Only tag and create a
  release after that squash-merge has actually landed on `main` - never
  tag a branch directly, and never tag/release before the merge.
- `main` is protected on GitHub (see repo Settings -> Branches): changes
  only land through a pull request with passing CI.
