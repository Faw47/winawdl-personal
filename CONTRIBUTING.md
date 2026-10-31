# Contributing

Changes are welcome: fixes, measurements from another machine, support for
another card, and corrections to anything written here that turned out wrong.
This is research code kept by one person in spare time, so an answer may take
a while.

## Before a pull request

Say what you ran and what you could not. Much of this project can only be
proved on the air, with the card and an Apple device, and a change that was
only compiled is still worth having if it says so.

What can be run with no radio:

```powershell
py -m unittest discover -s airdrop\tests   # the AirDrop layer, over loopback
python -m ruff check airdrop               # its style
.\scripts\build-app.ps1 -Check             # the app, off screen, against the real engine
.\awdl\tests\fake-radio.ps1                # the AWDL daemon against a stand-in radio
```

The installer is tried the same way, and only that way unless you mean to
install: `.\scripts\build-installer.ps1 -AllowDirty` gathers a trial copy
under `build\installer`, and `install.ps1 -DryRun` and `uninstall.ps1 -DryRun`
in it say what they would do. Without `-DryRun` they do it, at once if the
prompt is already elevated. Do not run one to see it refuse: check first
which rights the prompt has. With Inno Setup 6 installed the same script
also compiles the setup program, `installer\winawdl.iss`, which is a wizard
around those two scripts.

A dry run skips every step that changes something, so it cannot show a fault
in one, and three were found that way only when the installer was run for
real. Two habits come from that:

- Run the scripts with `powershell.exe`, which is Windows PowerShell 5.1 and
  what the launchers and setup start, and also with the 32-bit one in
  `SysWOW64`, since setup is a 32-bit program. PowerShell 7 does not behave
  the same.
- Try a step that removes or writes something on a throwaway folder first,
  with a few lines that do only that step.

`radio` and `awdl` build their test programs beside the daemons;
`scripts\build-radio.ps1` and `scripts\build-awdl.ps1` list them.

For a change that was tried on the air, give the numbers and how they were
taken: the file's size, the time, the channel, the other device and its OS
version. [DESIGN-NOTES.md](DESIGN-NOTES.md) is where such results are kept,
with the ones that did not work out.

## Keep other people out of it

A capture or a log taken near other people holds their devices' names and
addresses. Do not attach one as it is. `awdl-run.ps1 -Redact` and
`airdrop-receive.ps1 --redact` number devices instead of naming them.

## How the code is written

- C and C++ follow the `.clang-format` files. Python follows
  `airdrop\ruff.toml`.
- A comment says what the code cannot: a reason, a limit, something measured.
  It does not repeat the line under it.
- The app's look follows [app/DESIGN.md](app/DESIGN.md). A change to a
  screen comes with its pictures: `winawdl.exe --render <folder>` draws every
  state in light and dark, and `--drive <folder>` presses every control on
  the real window, off screen.
- A commit message says what changed and why in plain sentences, and what was
  measured if something was.

## Licenses

A contribution is under the license of the folder it goes in: GPL-2.0-only
in `radio`, GPL-3.0-or-later in `awdl`, MIT everywhere else. `radio` and
`awdl` must stay separate programs from each other and from the rest, since
their licenses do not mix. Changes to devourer itself go to
[the fork](https://github.com/raph559/devourer) on its `winawdl/pcie-windows`
branch, or upstream. Until a change is there, it can live here as a patch:
`radio/patches` for devourer and `awdl/patches` for OWL, made with
`git format-patch` against the submodule's commit and applied by the build to
a copy (`cmake/patched_copy.cmake`). When a submodule moves, drop the patches
it now carries.
