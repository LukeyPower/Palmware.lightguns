# Licensing & compliance

*Written 2026-07-05. This is a practical engineering assessment, **not legal advice** — I'm
not a lawyer. It records what the dependencies are licensed under and the reasoning behind
how this package is set up, so you can make an informed call (and show your work if anyone
ever asks).*

## TL;DR

- **Both** things this package builds on are **MIT-licensed** — the most permissive common
  license. You are allowed to use, modify, and redistribute them, provided their copyright +
  permission notices are retained (done in `THIRD-PARTY-NOTICES.md`).
- This package **does not redistribute** either dependency. It *depends on* the Blamcon
  package (installed separately) and *reimplements* the RS3 protocol from an MIT example.
- The one thing worth being careful about is the historical note that the Blamcon **serial**
  protocol was learned by "decompiling Blamcon ARC." That's been re-grounded to Blamcon's
  **publicly published** serial-command reference, which removes the concern for the shipped
  code. Details below.

## The dependencies and their licenses

### 1. Blamcon Lightguns for Unity — MIT (© 2025 Props3D)

- Repo: https://github.com/Props3D/UnityLightguns
- License verified from the package's own `LICENSE.md`: **MIT**.
- MIT grants use/copy/modify/merge/publish/distribute/sublicense/sell, on the sole condition
  that the copyright + permission notice ships "in all copies or substantial portions."
- **How we use it:** `Palmware.Lightguns.Blamcon` references the package's `Blamcon.Lightguns`
  assembly at compile time. We don't copy its source into this repo — the user installs the
  Blamcon package themselves. Since we don't ship "copies or substantial portions," the notice
  obligation is arguably not even triggered; we include the notice anyway (in
  `THIRD-PARTY-NOTICES.md`) because it's courteous and unambiguously safe.

### 2. Retro Shooter example game — MIT (© 2025 Grimbar Interactive LLC)

- License verified from the example project's `LICENSE.md`: **MIT**.
- **How we use it:** the RS3 serial command set in `RetroShooterModule` (`ZS`, `ZX`, `ZJ`,
  `ZM`, `Z0`–`Z5`, `ZZ`, `Z6`; COM port at 115200 8-N-1) comes from this example's
  `LightgunController.cs`. Our code is a rewrite into the module architecture, but because the
  constants and protocol are clearly derived from MIT-licensed source, we treat the RS3 module
  as an MIT derivative and **attribute Grimbar Interactive** in `THIRD-PARTY-NOTICES.md`.
- MIT explicitly permits this (modify + redistribute with notice), so the RS3 side is clean.

## The one thing to be careful about: "decompiling Blamcon ARC"

The original in-game code (in Croc Hunter / Spell Slinger) carried comments saying the Blamcon
**serial** protocol — the config/feedback commands `ZD.*`, `ZY.*`, `F.*`, the VID-3673 registry
discovery, `9600 8-N-1` — was "verified by decompiling Blamcon ARC." ARC is Blamcon's separate
desktop configuration app. That raised a fair question (the reason this doc exists): *is relying
on an ARC decompile a problem?*

Assessment:

1. **What we actually ship is fine.** We ship no ARC code, no ARC assets, and no verbatim
   Blamcon strings beyond short functional command tokens. Wire-protocol facts — the literal
   bytes/ASCII you must send to talk to a device — are generally **functional/interoperability
   facts, not copyrightable expression.** Reimplementing a protocol to interoperate with
   hardware you own is broadly accepted practice.
2. **The protocol is published anyway.** Blamcon documents these serial commands publicly at
   https://blamcon.com/get-started-with-blamcon/serial-commands/. So the decompile was never the
   *only* source, and it isn't a necessary one. The shipped comments have been **re-grounded to
   cite the public reference** instead of the decompile (see `BlamconSerial.cs`,
   `BlamconModule.cs`).
3. **The residual concern is narrow and historical.** The act of decompiling ARC itself could,
   in principle, run against ARC's own EULA if it has an anti-reverse-engineering clause. That's
   about a past act, not about anything this package distributes. To keep it a non-issue going
   forward: derive protocol details from the **public docs / the MIT RS example / observed serial
   traffic**, and keep any decompile-derived research notes out of the distributed package.

Net: **low risk for the shipped package.** The change made here — cite the public source, ship
no ARC-derived material — is the belt-and-suspenders version.

## Your own license

This package is released under **MIT** (`LICENSE.md`), © Palmware. That's compatible with both
MIT dependencies and keeps you free to use it in commercial games. If you ever want to keep it
private/proprietary instead, MIT dependencies allow that too — you'd just drop the public MIT
grant on your own code while still retaining the third-party notices.

## Checklist if you add another gun/SDK later

- Record the new dependency's license here and in `THIRD-PARTY-NOTICES.md`.
- Prefer official docs / permissively-licensed samples as your protocol source; avoid
  decompiling proprietary apps, and never ship material derived from one.
- If a dependency is **not** MIT/BSD/Apache (e.g. GPL, or a custom EULA), stop and check
  compatibility before taking a dependency on it — copyleft or "no redistribution" terms can
  change what you're allowed to do with the whole package.
