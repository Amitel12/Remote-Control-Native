# Phase 3 -- Input capture + injection

## Status

**Both halves implemented and real-hardware verified, including the
lesson #3 safety net, wired together end to end over the network (verified
on both loopback and a real two-machine network), and two real reliability
gaps found and fixed -- a stuck button/modifier (InputStateSync) and
garbled typed text (redundant send + InputSequenceDedup), the latter's
first attempt itself having a real bug only caught by actually measuring
the fix's effect. Not done -- see "What's not verified yet" below (the
three DPI/layout/physical-overshoot regression checks, and re-verifying
both reliability fixes on a real lossy network rather than only loopback/
synthetic conditions).**

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

## Real-hardware result: the end-to-end network loop

`RawInputCapture` (client) -> `InputEventCodec` -> a new `LanDatagramKind.Input`
datagram -> the existing LAN socket -> `InputEventCodec.Decode` -> `InputInjector`
(host) is wired up in `tools/LoopbackHarness`, opt-in via `--remote-input` on
both `--lan-client`/`--lan-host` (and the P2P equivalents). Reuses the same
UDP socket and `LanDatagramCodec` envelope already carrying video --
deliberately not ENet, matching the same "defer it until genuinely needed"
call `docs/PHASE-1.md` gate item 4 made for the video channel. Best-effort
UDP, same reliability as everything else on this socket -- see the open
question about that below.

Real-hardware loopback test (both host and client on this machine, so the
*same* physical cursor is being driven by two things at once -- see the
caveat below): moving the real mouse over the client's presentation window
visibly moved this machine's real cursor via the injector, confirmed by the
client's summary log: **`input-events-sent=59`** for one ~38s interactive
session, all real captured events, zero errors. On loopback specifically
the result looks like "teleporting" -- the cursor jumping around rather
than tracking smoothly -- because the client window and the host's full
captured display are different regions of the *same* physical screen with
the *same* physical cursor, so the real hand-driven position and the
injected recalculated absolute position fight each other. That's a
loopback-testing artifact, not a bug: on two separate machines there are
two separate physical cursors, so there's nothing to fight.

A real two-machine attempt hit `input-events-received=0` on the host --
turned out to be a process error, not a code bug: the client's build was
several commits behind (this feature hadn't been pushed yet when it was
tested), so `--remote-input` was silently unrecognized. Worth remembering
for next time: push and confirm the pulled commit hash matches *before*
asking the other machine to test a brand-new flag.

Once the client actually had the code (after `git pull` + rebuild), the
real two-machine run confirmed it: **`input-events-received=37`** on this
host, matching the client's own **`input-events-sent=37`** exactly -- zero
loss on this run -- from real mouse/keyboard activity on the second PC
over the actual home network. The exact gap this section is about, closed
for real, not just in theory. That same run's *video* side hit the
pre-existing idle-desktop capture-timeout issue (only 41/600 frames on the
host, 40/600 on the client, because this host's own screen sat idle during
the test) -- unrelated to input transmission, and not a new finding.

## Reliability: a real problem, found, fixed, and verified

`--drop-input-percent N` on the client (mirrors the video path's
`--drop-percent`) deliberately drops N% of captured input events before
sending, so a lost `MouseUp`/`KeyUp` specifically can be reproduced on
demand instead of hoping for it under generic network loss.

**Real two-machine result at 50% simulated loss confirmed this was a real
problem, not a theoretical one.** Typing `hello whatsup` arrived as
`helatsp` on the host -- every surviving character in correct relative
order, just roughly half silently missing. That's independent per-character
drop with zero recovery, exactly what best-effort UDP with no reliability
layer predicts. (A live mid-session check of this machine's real mouse
button state during the same test read `None` -- no stuck button that
time -- but with few genuine click attempts in that run, that result alone
wasn't strong evidence either way.)

**Fix**: `LanDatagramKind.InputStateSync` -- the client sends its current
held-button/held-key snapshot (`RawInputCapture.GetHeldMask()`, a `ushort`
bitmask, layout in `InputHeldStateMask.cs`) roughly every 300ms, subject to
the same `--drop-input-percent` simulation as everything else (realistic:
a real lossy network wouldn't spare sync packets either, and periodic
resend means one lost sync attempt just waits for the next). The host's
`InputInjector.ReconcileHeldState(mask)` releases anything it believes is
held that the mask says isn't -- self-healing a lost release within one
sync interval instead of leaving it stuck until session end.
Deliberately one-directional: it only ever *releases* on a mismatch, never
synthesizes a *press* the mask claims should exist -- a stale/reordered
sync causing a phantom press would be a worse failure than briefly waiting
for the user's next real input.

**Real-hardware automated verification** (`--input-reconcile-demo`, no
network or second machine needed -- isolates the reconciliation logic from
network conditions entirely): pressed a real left mouse button and a real
Control key, deliberately never sent the matching release, then called
`ReconcileHeldState` against an empty mask. `GetAsyncKeyState` (real OS
key-state, not internal bookkeeping) confirmed both were genuinely held
after press and genuinely released by reconciliation alone, with no
explicit up-event involved. All 4 checks passed.

## Typed-text loss: found, fixed, and empirically measured

`InputStateSync` only fixes *held state* -- it does nothing for a dropped
plain character keystroke (momentary, never tracked as "held"). Fix:
redundant send. Each captured event now goes out twice (immediately, then
~20ms later), tagged with a per-event sequence number, so the host can
apply only the first copy it sees of each and ignore the rest --
turning one independent loss chance `p` into `p^2` for both copies to be
lost.

**A new `--input-reliability-demo`** (real UDP sockets on loopback, real
per-send loss simulation, no GPU/window/second machine needed since this
isolates the mechanism itself) sends `"hello whatsup"` as real KeyDown
events and reconstructs what the host-side logic actually applies. First
version of this test found a **second real bug**, not just confirmed the
fix: a naive "only apply if this sequence number is greater than the last
one applied" gate meant a character's *KeyUp* (a numerically larger
sequence number, sent right after its KeyDown) could get applied before
the KeyDown's redundant retry arrived -- and the gate would then reject
that still-useful retry as "stale," because a *different*, unrelated,
larger sequence number had already gone through. Measured effect matched
the predicted cost almost exactly: `0.7 + 0.3*0.3*0.7 ≈ 76.3%` predicted
vs. `76.7%` observed, at 30% simulated loss -- redundancy was barely
helping at all.

Fixed with `InputSequenceDedup` (`tools/LoopbackHarness/InputSequenceDedup.cs`):
dedup by exact sequence-number *membership* in a bounded recently-seen
window (64 entries), not by strict increasing order -- reject only a true
duplicate, never a legitimate retry just because something else numerically
larger already went through. Shared between the real host code and the
demo, so both are provably running the same fixed logic.

**Real measured result, 30% simulated loss, averaged over 40 trials per
condition (a single 13-character run has too much variance to trust
alone)**: character-recovery rate went from **~70% without redundancy to
~90% with it**, matching the `1 - 0.3^2 = 91%` theoretical prediction for
the fixed dedup logic almost exactly. Confirmed consistent across five
separate runs (69.2-71.5% without, 89.4-91.9% with).

## What's not verified yet

- **None of `docs/ARCHITECTURE.md`'s three explicit Phase 3 regression
  checks have been run against the real, physical failure modes they
  describe**: (a) click/drag accuracy at screen edges on 125%/150% DPI
  scaling -- both demos so far ran at this machine's default DPI only; (b)
  typing English text with both an English and a non-English host keyboard
  layout -- only tested against an English layout so far; (c) fast drag
  overshoot + alt-tab mid-drag causing no stuck button on the host --
  the `--input-capture-demo` test proves the *focus-loss* half of this (see
  above) but used a synthetic off-screen window to steal focus, not a real
  physical alt-tab keystroke or a real fast-overshoot drag past the window
  edge; worth a manual pass with real hardware input specifically.
- **Neither reliability fix (InputStateSync or redundant-send) has been
  re-verified on a real lossy two-machine run yet** -- `--input-reconcile-demo`
  and `--input-reliability-demo` both prove their respective mechanisms
  correct in isolation (real sockets/real OS state, but loopback and
  synthetic loss), not the full loop (real capture -> real drop -> real fix
  -> real host, over an actual network) since either fix landed. Given that
  the *first* attempt at redundant-send had a real bug only caught by
  actually measuring the outcome, isolated-mechanism testing is good but
  not a substitute for that same kind of measurement on a real network.
- Redundant send only *reduces* loss (`p -> p^2`), it doesn't eliminate it
  -- at high enough loss (the 50% test rate that first surfaced this
  problem, not realistic conditions) some garbling will still occur. A
  genuinely reliable channel would need real retry/ACK or ENet.
