# The winawdl app: design direction

The direction below is the owner's. The second part lists what was decided
from it, one reason each. A change to the first part outranks anything in
the second.

## Direction

- **AirDrop on Windows** (2026-10-31, replacing "tactile and warm" of
  2026-10-12). The window looks and behaves like Apple's AirDrop: nearby
  devices as round faces, a transfer as a ring around the face, a request
  as an alert, almost no text. Apple's attention to detail is the bar.
- **Palette: neutral, with the Windows accent.** Light and dark, switching
  with the Windows setting. Surfaces are neutral greys as Apple's system
  colors are; the owner's Windows accent is the one accent.
- **Name:** winawdl. The window calls itself AirDrop, the thing it does.
- **Icon:** "more like an AirDrop-like icon" (the owner, on seeing the letter
  that first stood in for one).
- **Shell:** a native WPF window: one process, nothing downloaded, drag and
  drop that carries file paths. Chosen over WebView2 and Tauri on
  2026-10-31: Tauri draws with WebView2 on Windows, and either would add a
  browser engine to an app that sits in the tray all day.
- **A request out of the window** (2026-10-13): winawdl's own card in the
  corner of the screen, chosen over a Windows notification.
- **Always accepting a device** (2026-10-13): offered on the request, with a
  list in the settings.
- **Device pictures** (2026-11-01): the owner's own drawings of an iPhone,
  iPad and MacBook, converted shape for shape (src/Theme/Devices.xaml), on a
  white raised plate with a soft shadow, and a green dot beside "Visible as".
  The plate is an exception to "a shadow only on what floats".

Read as: a desktop utility window for one person handing files between their
own PC and the phones next to it.

### Rules every screen keeps

- **Type**, in Segoe UI Variable, the nearest Windows has to San Francisco:
  large title 26 bold, title 20 semibold, name 15 semibold, body 14,
  secondary 13, caption 12. Semibold only for names and headings.
- **Space** on an 8 px grid; 24 px from the window's edge.
- **Shape**: circles for devices, 12 px corners for cards, 8 for buttons,
  fields and thumbnails, 14 for the pill.
- **One material**: flat cards on the window with a hairline. No bevels, no
  sunken wells, nothing rotated. A shadow only on what floats above the
  window: menus and the cards in the corner of the screen.
- **Color**: the accent marks the next press and the progress of a
  transfer, nothing else. Every ink reaches 4.5:1 on every surface.
- **Words**: a person's words. "Receiving Off", not "Radio". Buttons in
  title case, as Apple writes them.

The sections below are from the first direction. Where they speak of
warmth, depth, wells or rotation, the rules above replace them.

## What follows from it

### The one idea

The window is a table. Devices sit across it, at the top. Your side is at the
bottom: what you are holding and what you have been given. Sending moves
things up, receiving moves them down. Every screen keeps that arrangement, so
where something is says whose it is.

### Depth

Three levels, and each means one thing.

| Level | Used for | Why |
| --- | --- | --- |
| Raised | devices, files, buttons | what can be pressed or picked up |
| Flat | text, labels | information, nothing to grab |
| Recessed | the drop well, the shelf of received files | where things land |

A shadow appears only on a raised thing, and a raised thing sinks when
pressed. Nothing floats for decoration. Light comes from above throughout: it
catches the upper edge of what is raised and the lower lip of what is cut in,
and a well is shaded under its upper lip. On the light table a shadow is a
darker paper (`#5A3F22`), not grey.

### Color

Warm neutrals carry the surfaces; they are what "warm" means here. The accent
is read from Windows at run time and marks one thing at a time: the next
press that moves a file, and the file while it moves. An accent that is too
pale or too dark for a surface is shifted in lightness until it reaches the
contrast below; its hue stays the owner's.

| Role | Light | Dark |
| --- | --- | --- |
| Table (window) | `#F4F0EA` | `#1C1A18` |
| Raised | `#FFFDFA` | `#2B2825` |
| Recessed | `#E8E1D7` | `#131110` |
| Ink | `#221E1A` | `#F2EDE6` |
| Soft ink | `#5C544B` | `#B8B0A5` |
| Line | `#D8CFC2` | `#3B3733` |
| Ember (a failure) | `#A8321A` | `#F2957A` |

Every ink, soft ink and ember pairing with every surface was measured: the
lowest is 5.16 to 1. Accent text has to reach 4.5 to 1 and an accent fill
3 to 1 against what it sits on, and text on an accent fill is whichever of
black and white reaches 4.5 to 1. With Windows contrast themes on, all of it
gives way to the system's own colors and the shadows go.

Dark mode exists because Windows has the setting and the direction says to
follow it. There is no switch in the app: the switch is the one in Windows.

### Type

Segoe UI Variable, the typeface Windows itself is set in, because the
direction is to belong on the machine. Its Display cut, drawn for large
sizes, carries the one line that is the whole screen; its Text cut carries
everything else. Numbers that change while you watch use tabular figures, so
a percentage does not shake as it counts.

| Use | Cut | Size | Weight |
| --- | --- | --- | --- |
| What is happening, when it is the whole screen | Display | 22 | Semibold |
| A device's name, a request, the line in the empty hand | Text | 17 | Semibold |
| Body | Text | 14 | Regular |
| Section labels | Text | 13 | Semibold |
| Secondary lines | Text | 12.5 | Regular |

Labels are written in sentence case. Nothing is set in capitals. The whole
window grows with the text size set under Accessibility in Windows, so that
larger text is never cut off by a frame that stayed small.

### Shape

Radius follows size, so a thing looks like what it is: 18 on a device plate,
which is hand sized; 10 on a button or a field; 8 on a file. A well's radius
is its content's radius plus its padding, so the two curves stay parallel: 22
for the hand, 18 for the shelf. Only the switch, the two round choices and
the button that takes a file back are fully round, because those are.

### Space

A unit of 4. Inside a plate, 16. Between plates, 10: close enough to read as
one group. Between your side and the devices, whatever is left: the gap is
the table.

### Icons

Segoe Fluent Icons, the set Windows draws its own interface with, for the
same reason as the typeface. An icon appears where it names a kind of thing
(a phone, a tablet, a computer; a page, a picture, a film, a link), or where
Windows itself uses that sign for that press: the gear for settings, the
cross that closes, stops or removes, the tick on a transfer that got through,
the shield on anything that leads to a permission prompt. A file shows its
own thumbnail when Windows has one.

### The app's icon

White rings spreading on blue, around a file turned the way the files in the
window are: a file that has just landed. Rings on blue are what make it read
as AirDrop at a glance, which is what was asked for. It is not Apple's mark,
which is theirs alone: that one is open at the bottom around a wedge, this
one is whole rings around a file.

- The blue is `#0078D4`, the accent Windows ships with. It stays that blue
  whatever the accent is, because an icon is pinned and found by its color.
- The plate is a shade lighter at the top than at the bottom: lit from
  above, like every raised thing in the window.
- A ring fades and thins as it spreads, and the widest is cut by the plate.
- Small sizes carry fewer, heavier rings (three from 48 pixels, two from 20,
  one below), so that each stays more than a pixel wide.
- The notification area gets the same mark in one color, white or black with
  the taskbar, as Windows' own marks there are.

The drawing is code, in `src/Shell/Chrome.cs`. After changing it, write the
icon file again and rebuild:

```powershell
.\build\app\winawdl\winawdl.exe --render icon app\winawdl.ico build\app\icon-preview.png
.\build\app\winawdl\winawdl.exe --render brand docs
```

The second line redraws the two pictures of the mark that the repository
shows: `docs\logo.png`, and `docs\social-preview.png`, the card a link to the
repository is drawn with. That card is the mark, the name, and one line set
in the app's own type on its own table color, and nothing else.

The README's two pictures of the app are drawn the same way, after a change
to a screen they show:

```powershell
.\build\app\winawdl\winawdl.exe --render readme docs
```

`docs\screenshot.png` is the window with the cards it shows outside itself,
at one scale and placed as on a screen. Nothing of Windows is drawn in it
but a plain ground and a strip where the taskbar would be.
`docs\cards.png` is the cards alone, in the light theme.

### Outside the window

A request that arrives while the window is not the one in front is shown as
the same raised card, in the corner of the screen above the taskbar, because
that is where Windows puts what wants an answer. It is the app's own card
and not a Windows notification: the build downloads nothing, and what would
draw one is not in the box. Three things follow from being a guest on
someone else's screen:

- It says whose it is, with the app's mark and name, which the window never
  has to.
- It never takes the keyboard, and it is not shown while a full-screen app
  or a presentation has the screen. The notification area says it then, as
  it does when the card is turned off.
- It holds Accept and Decline and nothing else. Always accepting a device is
  a longer thought, and is offered in the window.

It goes when the request is answered, when the sender stops waiting, and
when the window comes to the front, where the request already is.

What arrived is said the same way (the owner, 2026-10-13: a notice that
opens the file when pressed). That card is raised and pressed as a whole,
like a device's plate, because it is one thing leading to one place: a
single file opens, several are shown in their folder. It carries the file's
own picture, turned as files are in the window. It asks for nothing, so it
leaves by itself after 10 seconds, stays while the pointer is on it, and
has a cross for sooner. A request sits below it, nearest the corner: what
waits for an answer comes first to hand.

The icon in the notification area says under the pointer what is moving and
how far along, in the words the window uses, and how visible the PC is when
nothing moves (the owner, 2026-10-13).

### Motion

Every motion answers "where did it go?" or "did it take?".

| Motion | Says |
| --- | --- |
| A dropped file settles into the hand with a short overshoot | it landed, and here |
| A plate rises a little under the pointer and sinks when pressed | this is the thing being pressed |
| A plate or a request fills from the left during its transfer | how far along, on the thing it concerns |
| A received file drops onto the shelf | it is yours now, and here |
| A device or a request arrives by fading in and sliding a few pixels | the list changed, and which row |
| The card in the corner of the screen comes up from the edge it sits against | something new is here, and it came from outside |
| The settings slide in from the right | they lie over the table and go back the same way |
| The thumb of a switch slides across | which way it went |

Nothing loops, and nothing moves that did not change. With animation effects
turned off in Windows, every state changes at once and nothing travels. A
device that leaves, and a request that is answered, go at once.

### Words

Say who and what, in the order someone would say it aloud: "iPhone wants to
send you 3 photos". A failure names its cause and the next thing to do.
Buttons are plain verbs: Send, Accept, Decline, Open.
