# Security

## What is exposed

While it runs, this project reads data from anyone in radio range:

- the radio daemon and the AWDL daemon, which run with administrator rights,
  parse Wi-Fi frames from any nearby device;
- the AirDrop receiver accepts TLS connections from any device on the AWDL
  link and parses what it uploads: property lists, DVZip blocks and cpio
  archives, whose file names it turns into names on disk;
- the driver relays register accesses for a local program that runs as an
  administrator, and for no other.

A flaw in any of those is worth reporting. So is a way for the receiver to
store a file outside its folder, to overwrite one, or to open what it stored;
a way past the app's question before a file is taken; a way to make a
press on one of its cards on screen do something other than it says; a way
for a program without administrator rights to reach the driver; and anything
that setup changes on a PC and uninstalling does not undo.

[README.md](README.md#before-you-run-it) lists what the project does by
design, such as taking files from everyone when told to, or from any device
that gives the name and model of one you chose to always accept. Those are
not flaws.

## Reporting

Please do not open a public issue for a flaw that could be used against
someone. Use "Report a vulnerability" under the repository's Security tab,
which reaches the maintainer privately. Say what you sent, from what device,
and what happened; a capture helps, with other people's devices left out.

This is research code kept by one person. There is no bounty and no promised
response time, and no supported version other than the latest commit on
`main`.
