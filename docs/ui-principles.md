# UI principles

How the client's UI should behave, so a failure in one part never traps the user. Written after the passphrase card
(2026-10-25): a window made in a synchronous command deadlocked, the card stayed a white box, and Done sat on "Ending
administrator access…" with no way out. Every principle below would have turned that into a short delay.

## 1. Leaving never waits on anything that can hang

- Close, Cancel, Esc, Done, and a card's ✕ change the screen first (close the dialog, forget any secret it held) and
  tell the host afterwards, in the background. A failure arrives later as a toast that says what still happens on its
  own (for example "It ends by itself at 12:10").
- The window can always be closed: the title bar's close button goes through the app, but Alt+F4 is handled by Windows
  itself and works even when the app's command channel is stuck.
- A dialog that needs an answer before it can close (a form that saves) keeps Cancel enabled while it waits.

## 2. Every busy state has a deadline

- A step that waits on the host or a VM says so after about 20 seconds ("This is taking longer than usual") and offers
  Close. Pick the deadline from how long the step normally takes, not from the request timeout.
- Closing early must be safe. The host is the backstop (windows expire on their own, jobs keep running and are listed
  again), so the client can let go.
- A late answer to something the user has left must not act: no card, no dialog, no navigation. Keep a "dismissed" flag
  and check it after every await.

## 3. Optional features cannot break the main flow

- An extra (the passphrase card, a camera, a notification) is started after the main step succeeds, its errors are
  caught and ignored or shown as a note, and the main step never waits on it.
- Tauri commands that create or close windows must be async: in a synchronous command that deadlocks on Windows.
  `window_command_tests` in lib.rs fails the build otherwise.

## 4. The host is the source of truth

- Anything that outlives a dialog (an administrator window, a job, an elevation, a transfer) is read back from the host,
  not remembered by the page: when the list loads and when a menu that acts on it opens. A restarted client then shows
  the same state and can end it ("Admin until 12:10" in the VM row, End administrator access in its menu).
- The client keeps secrets only while they are on screen and asks the host again when it needs them (opening an
  administrator window that is already open returns the same passphrase).

## 5. Show state, do not make people remember it

- If the user has to carry information from one window to another (a passphrase into a Remote Desktop session), put it
  where they will look: the always-on-top card, not a dialog that the other window covers.
- Never put a secret on the clipboard: with clipboard redirection, a Remote Desktop session can read it.

## Conventions the client already follows

- Wording: plain sentences that say what happened and what to do; no slang and no em dashes (CLAUDE.md). Errors name
  the VM or host they are about.
- Features from a newer host are gated with `hostSupports(host, "x.y.z")`; with an older host the entry is hidden or
  says "(update host)".
- Elevated actions go through `withElevation`; cancelling the passphrase prompt is "nothing happened" (ElevationCancelled),
  never an error.
- Results that arrive after the user has moved on are toasts (`toasts.show`, `toasts.error`); progress that the user is
  watching stays in the dialog.
- Overlays start below the title bar (`--titlebar-height`), so the window can always be moved, minimized, and closed.
- Run the svelte-autofixer on every changed .svelte file. Its notes about timers inside `$effect` are expected for clocks
  and polling.

## Verifying

- Anything that creates windows, touches the operating system, or depends on focus is run in the packaged app before
  it is handed over: unit tests mock the Tauri window API and cannot see a deadlock. A throwaway build that triggers the
  feature at startup (never committed) is fine when the real path needs a host or a VM.
- Test the exits: for each dialog, a test where the host never answers and the user still gets out.
