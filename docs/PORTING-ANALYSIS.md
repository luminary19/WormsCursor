# Porting WormsCursor to macOS and Linux — Feasibility Analysis

> **Obsolete.** This analyses porting the old `SetSystemCursor`-based **cursor-theming engine**, which
> has since been removed — WormsCursor is now an agent-waiting overlay (no system-cursor theming), so
> the cross-platform blocker this document is about no longer applies. Kept for historical context; see
> `CLAUDE.md` / `README.md` for the current architecture.

> Produced by a multi-agent analysis pass (codebase mapping + platform API research,
> 2026-06-13). All codebase claims were verified against the source; platform claims
> were researched against current (2025/2026) API documentation and the Mousecape
> project. See [§9 Verified source references](#9-verified-source-references).

## 1. Executive summary (TL;DR)

WormsCursor's core feature — a **global, system-wide** swap of an animated cursor that
reacts to movement and clicks and is visible across **every** application — rests on the
Win32 `SetSystemCursor` API, for which **no clean, public equivalent exists on any other
platform.** On macOS it can only be achieved through undocumented private CoreGraphics
(CGS) APIs, exactly as **Mousecape** does: technically feasible, but fragile and broken by
Apple on every major release (confirmed regressions on macOS 26 Tahoe). On Linux/X11 it is
only an unreliable per-window workaround (any application that sets its own cursor
overrides ours), and on **Wayland it is a hard, protocol-level blocker** — a deliberate
compositor security decision that libraries cannot work around. On top of that, the entire
rendering layer (`System.Drawing.Common`) has been officially Windows-only since .NET 7 and
needs a migration to SkiaSharp, and the whole App layer (WinForms, HKCU autostart, named
`Mutex`, `SetWindowsHookEx`) needs a rewrite. **Verdict: the rendering and the surrounding
plumbing are portable and cheap — but they solve the non-blockers; the essence of the
product does not exist outside Windows.**

| Platform | Global cursor override | Rating | Primary blocker |
|---|---|---|---|
| **Windows** | ✅ full (`SetSystemCursor`) | 🟢 Green | — (current state) |
| **macOS** | ⚠️ private CGS API only (like Mousecape) | 🟡 Yellow | No public API; fragile, broken every macOS release; App Store reject |
| **Linux / X11** | ⚠️ unreliable per-window workaround | 🟡/🔴 | No `SetSystemCursor`; other apps overwrite the cursor; X11 is dying |
| **Linux / Wayland** | ❌ impossible | 🔴 Red | Protocol forbids it (no cursor outside your own surface) — hard blocker |

---

## 2. What is tied to Windows today

**Core mechanism (`CursorEngine.cs`) — BLOCKER.** The entire P/Invoke surface
(`user32`/`gdi32`/`winmm`) is Win32-only. The critical, non-portable primitives:

- `SetSystemCursor(hcur, OCR_*)` — global swap in the system slot table
  (`OCR_NORMAL`=32512, `OCR_HAND`=32649, `OCR_WAIT`=32514…). **No equivalent anywhere.**
- `SystemParametersInfo(SPI_SETCURSORS, …)` — restore by reloading the scheme from the
  registry (`HKCU\Control Panel\Cursors`). Win32-specific restore-on-crash install.
- `GetCursorInfo` + `LoadCursor(OCR_*)` (lines 454–471) — the idle optimization
  "re-render only while the cursor is on screen" (0% CPU at rest). **No equivalent on
  macOS/Linux** — nowhere can you ask "which of my cursors is the user currently seeing".
- `MakeCursor` (lines 637–647): `bmp.GetHicon() → GetIconInfo → CreateIconIndirect` —
  the HCURSOR-with-hotspot build pipeline is pure GDI+/Win32.

Portable inside the core: the movement/physics math (accumulation, `atan2`, hysteresis,
springs), `GetCursorPos` (equivalents exist), `GetAsyncKeyState` (needs an abstraction),
`timeBeginPeriod` (less relevant off Windows).

**Rendering (`ArrowRenderer`/`HandRenderer`/`ProgressRenderer`) — needs a rewrite, NOT a
blocker.** Verified usage of `System.Drawing.Drawing2D` (`GraphicsPath`, `SolidBrush`,
`Pen{LineJoin.Round}`, `Graphics.FromImage`, `ColorTranslator.FromHtml`).
`System.Drawing.Common 8.0.10` has been **officially Windows-only since .NET 7** (libgdiplus
abandoned; throws `TypeInitializationException` on non-Windows). The geometry is ~40% pure
math (preserved as-is); the rest is GDI+ calls with a near 1:1 mapping to SkiaSharp.
`HandShape.cs` (pure `float[]`), `CursorSettings.cs` (POCO) and `SettingsStore.cs`
(`LocalApplicationData` + `System.Text.Json`) are **100% portable**.

**App layer (`WormsCursor.App`) — needs a rewrite.** Entirely WinForms (`net8.0-windows`,
`UseWindowsForms`): `NotifyIcon`, `PreferencesForm` (783 lines), `ChangelogForm`. Plus
Win32/CLR-only: `RegistryAutostart` (`HKCU\…\Run` + `StartupApproved`), `SingleInstance`
(named `Mutex` + `EventWaitHandle`), `LowLevelKeyboardHook` (`SetWindowsHookEx(WH_KEYBOARD_LL)`),
Velopack `0.0.1298`. Bright spots: `IAutostart` is **already abstracted behind an
interface** (a good anchor point); `UpdateService` is ~80% portable (the GitHub API path is
plain `HttpClient` + `System.Text.Json`).

---

## 3. macOS — analysis

**Global cursor override: possible ONLY via private CGS APIs.**

- Public `NSCursor` is deliberately per-application/per-window: `[NSCursor set]` only works
  while your app is active and its window is frontmost; the system immediately resets the
  cursor once the mouse enters another `NSTrackingArea`/window. The "one app paints the
  cursor for the whole system" model is **impossible with public APIs.**
- The only route is the undocumented CGS (CoreGraphics Server) interface, verified in the
  **Mousecape** sources (`CGSInternal/CGSCursor.h`). The mechanism is fundamentally
  different from Windows: you don't swap an HCURSOR per slot — you **register images under a
  system cursor name with a `setGlobally` flag**:

  ```c
  CGError CGSRegisterCursorWithImages(CGSConnectionID, char *cursorName,
          bool setGlobally, bool instantly, CGSize, CGPoint hotspot,
          NSUInteger frameCount, CGFloat frameDuration, CFArrayRef imageArray, int *seed);
  CGError CoreCursorUnregisterAll(CGSConnectionID);   // = restore
  ```

  Cursor names are `com.apple.coregraphics.*` strings (`Arrow`, `IBeam`, `Wait`, `Move`…).
  **Animation is native** — the API takes `frameCount` + `frameDuration` + a CFArray of
  frames and the WindowServer plays them itself: **0% CPU during animation**, eliminating
  the whole "re-render only while on-screen" loop. Restore is cleaner than on Windows
  (`CoreCursorUnregisterAll` + per-session state in the WindowServer).

**Critical caveat — fragility.** SIP does **not** have to be disabled (Mousecape is
notarized), but the mechanism genuinely breaks on every macOS release: regressions on
macOS 26 Tahoe (Mousecape #287, #283 "cursor changes for a split second then reverts to
default", #261, #289); Tahoe introduced new identifiers (`ArrowS`, `IBeamS`,
`com.apple.cursor.26/20`), and PR #293 plans a migration to `SMAppService`. Apple could
close it off entirely. It also requires a privileged helper (launchd, `SMJobBless` →
`SMAppService`) for persistence, no App Sandbox (→ **Mac App Store ruled out**), and carries
an empirically unverified flicker risk when re-registering rotation frames tens of times
per second.

**UI / tray / packaging.** Avalonia `TrayIcon` maps to `NSStatusBar` in the menu bar (app
as `LSUIElement`, an agent with no Dock icon). Velopack has native macOS support (`.app`),
but **notarization is mandatory** (Apple Developer ID, $99/yr, `xcrun notarytool`).
Autostart: `HKCU\…\Run` → LaunchAgent plist or `SMAppService`. The global keyboard hook
(the I-beam "hop" effect while typing) → `CGEventTap`, requires the Accessibility
permission.

**Verdict — FEASIBLE-WITH-PRIVATE-APIS (yellow).** A hard proof of feasibility exists
(Mousecape: ~10 years old, notarized, global + animated), but it is a **continuously
maintained reverse-engineering hack**, not a weekend port. There is a standing cost to
reacting to Apple's per-release changes.

---

## 4. Linux — analysis

### X11 — technically possible, but architecturally foreign (PARTIAL / hack)

- Core X11 is **per-window**: `XDefineCursor(display, window, cursor)` only sets the cursor
  for a single window. **There is no native `SetSystemCursor`** — to change it "globally"
  you would have to call `XDefineCursor` on every top-level window of every client (you
  don't control other clients') or on the root window (which doesn't cover windows that
  define their own cursor).
- XFixes does **not** provide "set cursor globally" — only hiding/notifications
  (`XFixesSelectCursorInput`, `XFixesGetCursorImage`); `XFixesSetCursorName` only tags a
  name.
- `XcursorImageLoadCursor` (ARGB bitmap → `Cursor`, hotspot via `xhot`/`yhot`) is a real
  equivalent of the `GetHicon → CreateIconIndirect` pipeline — the **render → cursor-handle
  step is portable**. The problem isn't "how to make a Cursor", it's "how to force it
  globally".
- **An Xcursor theme ≠ a dynamic re-render.** Classic theming (`~/.icons`,
  `XCURSOR_THEME`, `index.theme`) is a static set of files loaded at application startup;
  changing it at runtime **does not propagate** to running apps (GTK/Qt read the theme at
  init — "restart your WM"). Animated `.Xcursor` files have a **fixed frame list** (`delay`
  ms) — they don't react to movement direction or clicks. The "arrow rotates to follow the
  mouse" effect is inexpressible as a theme file.
- The only realistic path: an `XQueryPointer` daemon (global position — **available on
  X11!**) → SkiaSharp render → `XcursorImageLoadCursor` → `XDefineCursor` on the root plus
  our own windows. BUT **any application that sets its own cursor (browser, terminal,
  editor) overrides ours**, and there is no idle optimization. "A demo over the desktop
  will work; a system-wide theme like on Windows — no."

**Verdict X11: PARTIAL / FEASIBLE-AS-HACK (yellow-red).** Works over the desktop and
windows that inherit the cursor; unreliable over modern applications. X11 is dying
(distros are moving to Wayland) → low return on investment.

### Wayland — hard blocker (BLOCKED)

- `wl_pointer.set_cursor` works **only while the pointer is over the client's own window**
  (it requires the `serial` from `wl_pointer.enter`). Over another app's window you don't
  even receive an `enter` event → you have neither the right nor the serial to set
  anything. This is a **deliberate architectural decision** — the cursor is the
  compositor's domain.
- `cursor-shape-v1` doesn't help (doubly): it works only over your own surface **and** only
  lets you pick one of 36 predefined named shapes — no custom or animated images.
- The remaining mechanisms are out too: **no global mouse position** (events are
  surface-local; `GetCursorPos` has no equivalent) and **no global keyboard hook**
  ("Wayland does not permit clients to globally bind hotkeys" → the I-beam "hop" effect is
  impossible).
- A "global" change is at most `XCURSOR_THEME`/`XCURSOR_SIZE` set **before** the session
  starts — a static theme, not runtime rotation. The only workaround is writing your own
  compositor — that is not "porting an app".

**Verdict Wayland: BLOCKED (red).** The security model rules out the essence of the
product. Because Wayland is the default in GNOME and KDE, the port would be dead for most
users.

**Tray/packaging/autostart (assuming the engine problem were solved):** Avalonia `TrayIcon`
via StatusNotifierItem/AppIndicator (native on KDE; **GNOME needs an extension**).
Packaging: AppImage (simplest), Velopack has Linux support (`.AppImage`). Autostart: an XDG
`.desktop` file in `~/.config/autostart` (the `IAutostart` interface is already there).
Single-instance: `flock`/unix domain socket. All of this **solves the non-blockers.**

---

## 5. Target cross-platform architecture

**The `ICursorPlatform` abstraction.** Separate two things: (a) the cursor abstraction —
strongly **leaky**, and (b) the host abstractions — clean. The key decision: the interface
**does not pretend** to a uniformity that doesn't exist — `Capabilities` is part of the
contract, and the host queries it and degrades the UI.

```csharp
public interface ICursorPlatform
{
    CursorCapabilities Capabilities { get; }       // GlobalCursorOverride: Windows only!
    void SetCursor(CursorRole role, CursorImage image);
    void RestoreAll();
    bool TryGetCursorPos(out int x, out int y);
    PointerButtons GetButtons();
    OnScreenCursor GetActiveCursor();              // LEAKS: idle-0%-CPU is Windows only
}
```

What **leaks** (the interface would be faking a uniformity that isn't there):
- `GlobalCursorOverride` exists **only on Windows** (macOS = own window / private CGS;
  X11 = per-window; Wayland = none).
- `GetActiveCursor` (idle 0% CPU) — **Windows only**.
- `GetButtons` without focus — leaks on Wayland.

What hides **cleanly**: `TryGetCursorPos`, plus the host interfaces `ITrayHost`,
`IAutostart`, `ISingleInstance`, `IUpdateService` — these are genuine seams (Velopack has
`.app`/`.AppImage`; autostart: HKCU/LaunchAgent/XDG; single-instance: Mutex/domain socket).
**`CursorImage` is a neutral BGRA32 buffer + hotspot** produced by Skia — not a `Bitmap`,
not an `HCURSOR`. `MakeCursor` (today in `Core`, lines 637–647) moves into
`WindowsCursorPlatform`.

**UI choice: Avalonia 11 (.NET 8) — recommended.** The only one of the three (vs .NET MAUI,
vs WinForms + separate UI) with native tray/menu on **all three** OSes (`TrayIcon` +
`NativeMenu` → Win / NSStatusBar / StatusNotifierItem) — and this app *is* a tray app. It
renders via SkiaSharp (consistent with the 2D choice). MAUI is out (second-class desktop,
no Linux, no tray). "WinForms + separate UI" only makes sense if we treat non-Windows as a
separate, reduced product anyway (Windows stays untouched, with no regression risk).

**Rendering migration to SkiaSharp.** Near 1:1 mapping: `Bitmap` → `SKBitmap`/`SKSurface`,
`GraphicsPath` → `SKPath` (`AddBezier` → `CubicTo`, `AddPolygon` → `MoveTo`/`LineTo`),
`SolidBrush`/`Pen` → `SKPaint`, transforms (`Save`/`Translate`/`Rotate`/`Restore`)
**identical**, `ColorTranslator.FromHtml` → `SKColor.Parse`. The geometry (the `k` scaling,
the quad→cubic fillet in `BuildPath`) is **unchanged**. Estimate: ~3–4 days + 1 day of
pixel-parity work (verifiable on Windows by comparing against GDI+).

**Decision: NOT "one identical product on 3 OSes". A shared core (rendering + physics +
settings + host interfaces) + thin per-OS host apps with an explicitly different,
partially impossible feature set.** Outside Windows this is a different, weaker product —
and we have to be honest about that.

---

## 6. The hardest problem

**The crux of the risk: outside Windows there is no clean, public API for the GLOBAL swap
of an animated system cursor.**

This is not a UI-framework problem nor the dead libgdiplus — it is the **absence of an OS
primitive.** `SetSystemCursor` gives a single system slot table seen by every application.
That model exists nowhere else:

- **macOS**: only private CGS (outside the App Store, broken every macOS release,
  WindowServer restrictions — the Dock takes over cursor control).
- **X11**: the cursor is a window attribute; "global" means overwriting other apps'
  windows, which you don't control.
- **Wayland**: the protocol **by design** forbids setting the cursor outside your own
  surface.

Consequences: (1) no framework or Skia fixes this — it is a hard boundary; (2) on macOS the
product depends on an API Apple openly breaks (standing maintenance cost, risk of total
shutdown); (3) on Wayland (the default in modern distros) the product is **dead**; (4) the
idle-0%-CPU property is also lost (no `GetActiveCursor` off Windows), degrading even the
reduced versions. Conclusion: the rendering *logic* is portable — the *essence of the
product* is not.

---

## 7. Roadmap and effort estimate

| Phase | Scope | Effort | Priority |
|---|---|---|---|
| **0. Abstraction + Skia on Windows** | Extract `ICursorPlatform` + host interfaces; rendering GDI+ → SkiaSharp (returns `CursorImage`); `MakeCursor`/HCURSOR → `WindowsCursorPlatform`. Zero behavior change, pixel-parity with GDI+. | **M** (~1 week) | **CHEAP QUICK WIN — do it** |
| **1. Avalonia as the shared host** | Preferences + Changelog → Avalonia XAML; tray via `TrayIcon`/`NativeMenu`; Windows variants of the host interfaces. The changelog Markdown parser ports 1:1. | **L** (~1–2 weeks) | Conditional (only if targeting beyond Windows) |
| **2. macOS PoC** | `MacCursorPlatform` (NSCursor over our own window; position via `CGEventSource`); a menu-bar app with preview/Showtime; LaunchAgent; Velopack `.app` + notarization. *Research:* an isolated private-CGS global prototype (out of App Store). | **L** (~2–3 weeks; most of the risk is the empirical CGS/flicker verification) | After Phase 1, go/no-go after the PoC |
| **3. Linux/X11** | `X11CursorPlatform` realistically = an Xcursor theme generator + switching (static theming); follow-the-mouse rotation **out of scope**. Tray via StatusNotifierItem (detect the DE); XDG autostart; `.AppImage`. | **M/L** | Low (X11 is dying) |
| **4. Wayland** | A separate track, not a continuation. Realistic scope: a static theme via `XCURSOR_*` env, or a deliberate "unsupported". No animation promises. | — | **DEFER / DROP** |

**Recommended order:** 0 → (strategic decision) → 1 → 2. Phase 0 is valuable **regardless
of the port** (it cleans up the architecture, removes Win32 from the core, and is
verifiable on Windows). Drop Wayland.

---

## 8. Final recommendation

**Do it partially — and only in a specific order, with no promise of Windows parity.**

1. **Do Phase 0 unconditionally** (the `ICursorPlatform` abstraction + the SkiaSharp
   migration, on Windows alone). It is a cheap quick win: it removes the dependency on the
   abandoned `System.Drawing.Common`, cleans up the architecture, is 100% verifiable on
   Windows (pixel-parity), and carries zero regression and zero platform risk. **Valuable
   even if the port never ships.**

2. **macOS — build only as a menu-bar app with preview/demo** (theming over our own
   window), with an optional, explicitly-marked-unstable CGS global experiment. Basing full
   system theming on private APIs is a conscious decision to accept a **standing maintenance
   cost** (Apple breaks it every release) and to give up the Mac App Store. Acceptable only
   if the team is willing to play "racing against Apple" — otherwise ship an honest,
   reduced demo.

3. **Linux — at most X11 as a static Xcursor theme generator**, clearly communicating the
   lack of follow-the-mouse rotation. **Drop Wayland** — it is a hard protocol blocker, not
   workable without building your own compositor, and it is the default in modern distros.

**Why not a "full port":** the essence of the product — a global, dynamic, animated cursor
across all applications — is **Windows-only by design.** Outside Windows it either doesn't
exist (Wayland), or is an unreliable workaround (X11), or is a fragile private-API hack
(macOS). Investing in "full cross-platform parity" is unjustified; investing in a **clean
abstraction + Skia on Windows** is — because it improves the current product and opens a
cheap option for reduced versions, should they ever be needed.

> **Note on `CLAUDE.md`:** the document's "Core stays UI-free / portable" framing is
> partly a myth in practice — `System.Drawing.Common` has been Windows-only since .NET 7,
> so even the UI-agnostic `Core` will not build off Windows today. And the real blocker is
> not in the UI layer but in the absence of an OS primitive for the global cursor swap.

---

## 9. Verified source references

Files verified to support this report (absolute paths):

- `src/WormsCursor.Core/CursorEngine.cs` — P/Invoke (lines 666–705), `MakeCursor`/HCURSOR
  (637–647), `RestoreDefaultCursors` = `SPI_SETCURSORS` (663–664), idle-query
  `GetCursorInfo` + `LoadCursor` (454–471).
- `src/WormsCursor.Core/ArrowRenderer.cs` — pure `System.Drawing.Drawing2D`
  (GraphicsPath/Pen/SolidBrush/ColorTranslator); geometry separated from the backend.
- `src/WormsCursor.Core/WormsCursor.Core.csproj` — `net8.0-windows`,
  `System.Drawing.Common 8.0.10`.
- `src/WormsCursor.App/WormsCursor.App.csproj` — `net8.0-windows`, `UseWindowsForms`,
  `Velopack 0.0.1298`.
- `src/WormsCursor.App/Autostart.cs` — `IAutostart` already abstracted (a ready anchor
  point for LaunchAgent/XDG), `RegistryAutostart` = `HKCU\…\Run`.

External research anchors: the **Mousecape** project (`CGSInternal/CGSCursor.h`, issues
#261/#283/#287/#289, PR #293) as proof of macOS feasibility-via-private-API; the Wayland
`wl_pointer.set_cursor` / `cursor-shape-v1` specs as proof of the Wayland blocker; the
.NET 7 `System.Drawing.Common` Windows-only breaking change.
