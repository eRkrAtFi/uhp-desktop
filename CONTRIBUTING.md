# Contributing

This repository holds two related projects: `riscv-emu/` (a hand-written
RV64 emulator, differentially verified against real QEMU) and
`QemuManager/` (the "UHP Desktop" .NET/WPF app that manages both QEMU and
riscv-emu environments). `vision/VISION.md` describes where the project
is headed; `vision/NEXT.md` tracks what the next step actually needs.

## Branches

- Nothing is committed or pushed directly to `main`.
- Every phase of work or larger change gets its own branch:
  `phase/<name>` for a roadmap step, `fix/<name>` for a smaller,
  self-contained fix. Commit to it as often as you like, and push it at
  any point as a backup - it does not need to be finished first.
- `main` is protected on GitHub: it only accepts changes through a pull
  request with passing CI (see repo Settings -> Branches).

## Before opening a pull request

Get the change green locally first:

- **QemuManager/UHP.Core**: run `QemuManager/finish.ps1` from a
  developer shell (builds `UHP.Core`, `QemuManager.SelfTest`, and
  `QemuManager` itself; runs the full SelfTest suite; publishes; prints
  the version actually baked into the published `.exe` so a summary can
  end with something verified, not just claimed).
- **riscv-emu**: from `riscv-emu/`, `make test-all` (the fast suite: unit
  + differential + integration). `make compare` alone is the
  differential check against real `qemu-system-riscv64`; `make
  boot-test`/`make interactive-test`/`make fs-compare` additionally need
  a real kernel+initramfs under `boot/` (see `scripts/fetch-guest-images.sh`).

A change touching both projects should have both green before a pull
request is opened.

## Pull requests

1. Open a pull request from your branch into `main`, with a summary of
   what changed and why.
2. Wait for CI to go green on the pull request.
3. The repo owner squash-merges it themselves, via the Merge button, once
   they choose to - that click is the approval. There is no separate
   approving review to request: a solo repo has no second person who
   could approve the author's own PR, so branch protection requires 0
   approving reviews and relies on the green `build-and-test` check plus
   the merge itself instead. Claude never merges a pull request it
   opened, regardless of how green CI is.
4. Only after that squash-merge has landed: tag the release (e.g.
   `vX.Y.Z`) and push the tag. Pushing a tag triggers
   `.github/workflows/release.yml`, which builds and publishes the
   release. Never tag a branch directly, and never tag ahead of the
   merge.

## CI

`.github/workflows/ci.yml` runs on pull requests targeting `main` and on
pushes to `main` itself (i.e. after a merge) - not on every push to a
working branch. Push a branch as often as you like without triggering a
full CI run; open the pull request when you want CI to check it.

## Screenshots and other images

Claude never adds, replaces, or edits an image committed to this
repository (`docs/`, README images, any `.png`/`.jpg`/`.gif`) on its
own, even to fix one it notices is stale or wrong - a screenshot it
captured unsupervised has repeatedly leaked things a published repo
should not show (the real machine's hostname/username in the app's own
title bar, a badly cropped window edge).

The actual process: Claude prepares the app (starts it with a demo
Environment already set up) and says exactly where to save the file.
The person takes the screenshot themselves, crops and checks it, and
only then explicitly says "screenshot schválený" ("screenshot
approved"). Only after that does Claude commit that exact file,
unmodified - never a screenshot it captured itself.
