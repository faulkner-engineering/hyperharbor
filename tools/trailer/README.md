# Trailer recorder

Records the trailer of HyperHarbor's main flow: make a machine profile, then create a VM from it. A script moves the real
mouse, clicks, and types in the portable client while ffmpeg records the client's content area, then cuts the recording
into an animated WebP.

## What the trailer shows is made up

`demo.js` is loaded into the client for the run, and the page is reloaded without it at the end. It intercepts
Tauri's IPC (each command is a `fetch` to `http://ipc.localhost/<command>`):
- One host, renamed HOME-PC, with a made-up address. Other paired hosts are hidden.
- Only the VM named Faulkner Engineering LLC is listed, with a made-up address.
- Elevation always reads as active, so the passphrase prompt never appears.
- Saving the profile and creating the VM happen in the page only. The create job is a fake that reports the host's
  real step names, and the new VM is shown as installing. Nothing is written to the host.

The catalogs, package search, ISO list, and the profile editor's data still come from the real host.

## Requirements

- Windows with a paired local host running, and a portable client in `dist` (`scripts\package.ps1 -Fast -ClientOnly`).
- Node 22 or later (built-in WebSocket).
- ffmpeg: `winget install Gyan.FFmpeg`, or on PATH, or the `FFMPEG` environment variable.
- An unlocked desktop: a locked or sleeping screen records black.

## Run

```
powershell -ExecutionPolicy Bypass -File tools\trailer\make-trailer.ps1 -Dry
powershell -ExecutionPolicy Bypass -File tools\trailer\make-trailer.ps1
```

`-Dry` runs the clicks and typing without recording and stops before Save. Do not touch the mouse during a run.

A status bar just below the client window (outside the recording) shows the stage: getting ready, a red REC timer
while it records (making the profile, creating the VM, showing the new VM), amber while it encodes after recording
stops (the mouse is free again), and green Done with the file and its size. It closes about 8 seconds later.

Output goes to `tools\trailer\out\` (git-ignored): `raw.mp4` (lossless), `cuts.json`, and `hyperharbor-trailer.webp`
(1200 pixels wide, 15 fps, played 1.5 times faster, WebP quality 85, about 7 MB). To cut or encode it differently, run `make-gif.ps1`
with your own `-Cuts` (each `start-end[@speed]` in seconds), `-Speed`, `-Width`, `-Fps`, and `-Quality`. An `-Out`
ending in `.gif` writes a GIF instead, but the orbiting model makes a GIF of this length about ten times larger.
The main README shows the copy in `docs/images/hyperharbor-trailer.webp`.

Options of `make-trailer.ps1`:
- `-Window x,y,width,height`: the client window's outer frame in physical pixels (default `40,10,1720,1040`, for a
  1920x1200 screen at 150%). Keep it clear of anything on the desktop that should not be recorded, such as the
  "Activate Windows" watermark in the lower right.
- `-ProfileName`, `-Vm`: the names typed into the editor and the Create VM dialog.
- `-Port`: the WebView2 debug port (default 9333). `-Out`: the output folder.

## The debug port

`make-trailer.ps1` restarts the client with `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=<port>`
when the port is not already open. While the port is open, any local process can drive the client, so use it for
recording only and restart the client normally afterwards.

## Files

- `make-trailer.ps1`: starts the client with the debug port and runs `drive.mjs`.
- `drive.mjs`: the scene. It finds each element through the Chrome DevTools Protocol, glides the mouse to it, clicks,
  types with `Input.insertText`, records with ffmpeg's gdigrab, and plays the create job in about 4 seconds.
- `demo.js`: the demo layer described above.
- `cdp.mjs`: a minimal DevTools Protocol client; `node cdp.mjs eval "<expr>"` or `node cdp.mjs shot <file.png>`
  help when the UI changes and a step no longer finds its element.
- `status.ps1`: the status bar, a click-through window that never takes focus; it reads `out\status.txt`.
- `mouse.ps1`: places the window and moves and clicks the mouse (user32), driven over stdin by `drive.mjs`.
- `make-gif.ps1`: cuts, speeds up, and encodes the animated WebP (or a GIF with a generated palette).

The steps find elements by their visible text (button labels, field labels, placeholders, winget ids), so a
renamed label in the client needs the same change in `drive.mjs`.
