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
  only land through a pull request with a passing `ci-ok` check (a
  summary job that always runs and passes once every other job either
  succeeded or was cleanly skipped - see `.github/workflows/ci.yml`);
  force-pushes and branch deletion are disabled. No approving review
  count is required (there is no second reviewer to require it from) -
  the merge button itself is the approval.

## Documentation

- Every pull request that changes functionality (not a pure refactor,
  test, or CI change) also updates the root [`README.md`](README.md)'s
  own Status section - what works and what doesn't - in the same PR,
  not as a separate follow-up. If a `CHANGELOG` file exists by the time
  you read this, update it the same way, in the same PR.
- A README/doc describing a point-in-time state (an audit, a phase-by-
  phase build log) is not expected to be rewritten after the fact - add
  a dated note or a short "since then" correction instead of editing
  old prose, the same way `QemuManager/ARCHITECTURE_AUDIT.md` and the
  phase sections in `riscv-emu/README.md` already do. The root
  `README.md`'s own Status section is the one place that must always
  describe the current state, not a historical one.

## Screenshots and other images

- Never add, replace, or edit an image committed to this repository
  (`docs/`, README images, any `.png`/`.jpg`/`.gif`) without being asked
  to - not even to fix a stale one noticed in passing. A screenshot
  captured unsupervised has repeatedly leaked things a published repo
  should not show (the real machine's hostname/username in the app's
  own title bar, a badly cropped window edge) - this happened more than
  once before this rule existed.
- The actual process: Claude prepares the app (starts it with a demo
  Environment already set up) and tells the user exactly where to save
  the file. The user takes the screenshot themselves, crops and checks
  it, and only then explicitly says "screenshot schválený" ("screenshot
  approved"). Only after that does Claude commit that exact file,
  unmodified - never a screenshot Claude captured itself.
