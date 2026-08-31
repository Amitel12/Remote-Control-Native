# Phase 3 -- Input capture + injection

## Status

**Injection half implemented and smoke-tested on real hardware. Capture half
does not exist yet. Not done -- see "What's not verified yet" below before
treating any part of this phase as complete.**

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
button/modifier (the host-side complement to lesson #3's capture-side
pointer-capture net, which doesn't exist yet -- see below).

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

## What's not verified yet

- **`RemoteControl.Input.RawInputCapture` does not exist.** Nothing captures
  real local mouse/keyboard input yet, so there is no real end-to-end
  capture -> encode -> network -> decode -> inject loop -- today's test only
  exercises the injection half, standalone, on one machine.
- **None of `docs/ARCHITECTURE.md`'s three explicit Phase 3 regression
  checks have been run**: (a) click/drag accuracy at screen edges on
  125%/150% DPI scaling -- today's demo ran at this machine's default DPI
  only; (b) typing English text with both an English and a non-English host
  keyboard layout -- only tested against an English layout so far; (c) fast
  drag overshoot + alt-tab mid-drag causing no stuck button on the host --
  untested (`ReleaseAllHeld()` exists as the safety net but nothing has
  exercised the failure mode it's meant to catch).
- **Lesson #3's capture-side half (pointer capture + blur/disconnect
  force-release) is not implemented** -- only the host-side `ReleaseAllHeld()`
  half exists. The real risk lesson #3 describes (a fast pointer overshoot
  or alt-tab losing the eventual button-release) can only happen on the
  capture side, which doesn't exist yet.
