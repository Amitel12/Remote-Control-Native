# Phase 3 -- Input capture + injection

## Status

**Both halves implemented and real-hardware verified, including the
lesson #3 safety net. Not done -- see "What's not verified yet" below
before treating this phase as complete (none of the three explicit
regression checks have been run).**

`RemoteControl.Input.InputInjector` replays `RemoteControl.Protocol.InputEvent`
via `SendInput` (real Win32 P/Invoke, `Win32Native.cs`, no package). Bakes in
`docs/ARCHITECTURE.md`'s lessons #1 and #2 from the start:

- **Physical pixels end-to-end (#1)**: callers pass the target display's
  bounds in physical pixels; `InputInjector` denormalizes the wire format's
  0..1 coordinates against those bounds, then re-normalizes against the
  *full virtual desktop* (`SM_XVIRTUALSCREEN` etc.) for
  `MOUSEEVENTF_ABSOLUTE|VIRTUALDESK`, so a target display at a
  negative/offset position (a secondary monitor to the left, say) still
  lands correctly.
- **Keyboard layout independence (#2)**: a `KeyKind.Character` key with no
  modifier held is plain typed text -- `KEYEVENTF_UNICODE` unconditionally,
  never VK/scan-code translation. A `KeyKind.Character` key *with* a
  modifier held (Ctrl+C, Ctrl+A, ...) is different on purpose: Unicode
  injection bypasses the OS's normal shortcut handling entirely (Ctrl+C
  typed as Unicode does not copy), so that specific case routes through
  `VkKeyScanEx` (genuinely layout-dependent, deliberately) to resolve which
  VK the live keyboard layout maps the letter/digit to -- the modifier
  itself is a separate, real `NamedKey` press already held down by the time
  this fires. `KeyKind.Named` keys always use real VK+scancode injection.

`InputInjector` also tracks which buttons/named-keys it believes are
currently held and exposes `ReleaseAllHeld()` -- a host-side safety net so a
dropped connection mid-drag or mid-shortcut can't leave a stuck virtual
button/modifier.

`RemoteControl.Input.RawInputCapture` is the other half: captures real local
mouse/keyboard input on a given window (classic Win32 subclassing --
`SetWindowLongPtr(GWLP_WNDPROC)` -- so it works with any window handle,
WinForms today or a future WPF `HwndSource`) and raises it as normalized
`InputEvent`s. Despite the name, it deliberately does *not* use the Win32
"Raw Input" API (relative deltas, meant for FPS-style look controls) -- the
wire format's `MouseMove` is an absolute normalized position, so the right
primitive is the ordinary `WM_MOUSEMOVE`/`WM_*BUTTON*` messages. Lesson #3
(mouse capture during drag) is load-bearing here: `SetCapture` on
button-down keeps mouse messages arriving even once the cursor leaves the
window (fast overshoot), and `WM_KILLFOCUS` (losing focus altogether --
alt-tab mid-drag) force-releases every currently-held button/modifier so a
dropped local button-up can never leave the remote host with a stuck
virtual button. Plain character typing is read from `WM_CHAR` (Windows
already resolves the correct localized/shifted Unicode character) rather
than translated from `WM_KEYDOWN`'s VK -- except when Ctrl/Alt is held,
where Windows delivers a C0 control code via `WM_CHAR` instead of the
letter (Ctrl+C is `0x03`, not `'c'`), so that case reads the letter
straight off the VK instead and marks it with the held modifier, feeding
`InputInjector`'s matching `VkKeyScanEx` shortcut path on the other end.
The VK↔`NamedKey` table is shared between both classes
(`NamedKeyMapping.cs`) so they can't drift apart.

## Real-hardware result

`tools/LoopbackHarness --input-demo` (5s countdown, then a scripted
sequence) against Notepad on this machine, RTX 3070 / Windows 11, default
English keyboard layout, default DPI:

- Mouse moved through all four quadrants of the target display plus center,
  then a real left-click, all landing correctly.
- Typed `Hello from InputInjector -- unicode typing test: héllo, 日本語, emoji 🎉`
  via `KEYEVENTF_UNICODE`. Result in Notepad: `日本語` and 🎉 (the hard
  case -- a surrogate-pair codepoint, proving `char.ConvertFromUtf32` +
  per-UTF-16-unit injection works) came through byte-for-byte correct.
  `héllo` arrived as `hello` and `unicode` as `Unicode` -- **not an injector
  bug**: `KEYEVENTF_UNICODE` input rides the same text-input pipeline real
  typing does, so a target app's own autocorrect/autocapitalize (Windows 11
  Notepad has both) can alter injected text exactly as it would alter a
  human's typing. Worth knowing, not worth fighting -- a genuine injector
  should not try to defeat the target application's own text services.
- Real VK+scancode `Enter` produced an actual new line (not just a typed
  character).
- Ctrl+A (held `NamedKey.Control`, then `Character 'a'` with
  `ModifierKeys.Control` routed through `VkKeyScanEx`) actually selected all
  text -- confirmed by the user copy/pasting the selected content back out,
  which only works if the selection was real.

## Real-hardware result: capture (+ the lesson #3 safety net)

`tools/LoopbackHarness --input-capture-demo` is self-verifying: it uses
`InputInjector` to synthesize real OS input into a `RawInputCapture`-hooked
window and checks what comes back, rather than requiring a human to type
into it and eyeball the result. RTX 3070 / Windows 11, default English
layout, default DPI:

- A plain move + click round-tripped correctly.
- **The lesson #3 safety net fired for real**: the script pressed the left
  button, moved (a drag), then shifted OS focus to a second (off-screen)
  window *without ever sending a button-up* -- and `RawInputCapture`'s
  `WM_KILLFOCUS` handler synthesized the missing `MouseUp` on its own,
  confirmed in the captured-event log at the exact placeholder position
  `ForceReleaseAll()` uses. This is the actual failure mode lesson #3
  describes (a real disconnect/blur mid-drag), reproduced and caught, not
  just an untested code path.
- A wheel scroll round-tripped.
- Typing `hi 🎉` round-tripped correctly, including the surrogate-pair emoji
  arriving as one correct codepoint (`U+1F389`) -- proof the high/low
  surrogate buffering in `HandleChar` works against genuine OS-delivered
  `WM_CHAR` messages, not just synthetic test input.
- Ctrl+A captured as `Character 'a'` with `ModifierKeys.Control` held --
  *not* the raw `0x01` control code Windows would otherwise deliver via
  `WM_CHAR` for that combination -- confirming the VK-range shortcut path in
  `HandleKeyDown` resolves the letter correctly instead.
- Minor, harmless observation: a few duplicate `MouseMove` events appeared
  at the same position, most likely `WM_MOUSEMOVE` messages the OS resent
  around window-activation. Didn't affect any of the above; not investigated
  further.

## What's not verified yet

- **None of `docs/ARCHITECTURE.md`'s three explicit Phase 3 regression
  checks have been run against the real, physical failure modes they
  describe**: (a) click/drag accuracy at screen edges on 125%/150% DPI
  scaling -- both demos so far ran at this machine's default DPI only; (b)
  typing English text with both an English and a non-English host keyboard
  layout -- only tested against an English layout so far; (c) fast drag
  overshoot + alt-tab mid-drag causing no stuck button on the host --
  today's automated test proves the *focus-loss* half of this (see above)
  but used a synthetic off-screen window to steal focus, not a real
  physical alt-tab keystroke or a real fast-overshoot drag past the window
  edge; worth a manual pass with real hardware input specifically.
- **No real end-to-end loop yet**: `RawInputCapture` and `InputInjector`
  are each proven to work correctly on their own machine, but nothing wires
  capture -> `InputEventCodec` -> a network channel -> decode -> inject
  together across two machines yet -- that integration, plus the actual
  ENet reliable/unreliable input channels from `docs/WIRE-PROTOCOL.md`,
  remains.
