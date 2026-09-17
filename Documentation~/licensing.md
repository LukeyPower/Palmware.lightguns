# Licensing & compliance

*Written 2026-07-05. This is a practical engineering assessment, **not legal advice** — I'm
not a lawyer. It records what the dependencies are licensed under and the reasoning behind
how this package is set up, so you can make an informed call (and show your work if anyone
ever asks).*

## TL;DR

- **Both** things this package builds on are **MIT-licensed** — the most permissive common
  license. You are allowed to use, modify, and redistribute them, provided their copyright +
  permission notices are retained (done in `THIRD-PARTY-NOTICES.md`).
- Since 0.4.0 this package **redistributes (vendors) the Blamcon package's Runtime code**
  under its MIT terms, with the required notice retained in `THIRD-PARTY-NOTICES.md`.
  The RS3 protocol remains a *reimplementation* from an MIT example (no code shipped).
- The Blamcon **serial** protocol used here is Blamcon's **publicly published** serial-command
  reference — all protocol information came from public sources. Details below.

## The dependencies and their licenses

### 1. Blamcon Lightguns for Unity — MIT (© 2025 Props3D)

- Repo: https://github.com/Props3D/UnityLightguns
- License verified from the package's own `LICENSE.md`: **MIT**.
- MIT grants use/copy/modify/merge/publish/distribute/sublicense/sell, on the sole condition
  that the copyright + permission notice ships "in all copies or substantial portions."
- **How we use it (0.4.0+):** the package's `Runtime` source is **vendored** into this repo at
  `Runtime/Blamcon/Device/`, currently from upstream **2.0.0** (commit `1e8992d`; 0.4.0 shipped
  1.0.0). The source files are unmodified; only upstream's asmdef, `AssemblyInfo.cs` (test
  visibility) and tests/samples are left out — the code compiles into our own
  `Palmware.Lightguns.Blamcon` assembly (`allowUnsafeCode: true`, which upstream now also
  sets). This is squarely within MIT's grant (copy/modify/merge/distribute), and it
  triggers MIT's one condition — the copyright + permission notice must ship with the code —
  which is satisfied in `THIRD-PARTY-NOTICES.md` (provenance, commit, and modification are
  recorded there too). Prior to 0.4.0 we only referenced the package's assembly and the user
  installed it separately.

### 2. Retro Shooter example game — MIT (© 2025 Grimbar Interactive LLC)

- License verified from the example project's `LICENSE.md`: **MIT**.
- **How we use it:** the RS3 serial command set in `RetroShooterModule` (`ZS`, `ZX`, `ZJ`,
  `ZM`, `Z0`–`Z5`, `ZZ`, `Z6`; COM port at 115200 8-N-1) comes from this example's
  `LightgunController.cs`. Our code is a rewrite into the module architecture, but because the
  constants and protocol are clearly derived from MIT-licensed source, we treat the RS3 module
  as an MIT derivative and **attribute Grimbar Interactive** in `THIRD-PARTY-NOTICES.md`.
- MIT explicitly permits this (modify + redistribute with notice), so the RS3 side is clean.

## Blamcon serial protocol provenance

The Blamcon serial config/feedback commands used in `BlamconSerial` (`ZD.*`, `ZY.*`, `F.*`, the
VID-3673 registry port discovery, `9600 8-N-1`) come from Blamcon's **publicly published**
serial-command reference: https://blamcon.com/get-started-with-blamcon/serial-commands/. The
port-discovery approach (enumerate Windows' registry for the gun's VID) is standard OS practice.
Code comments in `BlamconSerial.cs` and `BlamconModule.cs` cite that public reference.

Two points worth keeping in mind:

1. **These are functional interoperability facts.** Wire-protocol tokens — the literal ASCII you
   send to talk to a device — are generally not copyrightable expression, and reimplementing a
   protocol to interoperate with hardware you own is standard, accepted practice.
2. **We ship no third-party material.** The package contains no Blamcon application code or
   assets — only our own implementation calling a documented protocol.

Net: **low risk for the shipped package** — all protocol information is from public sources.

## Your own license

This package is released under **MIT** (`LICENSE.md`), © Palmware. That's compatible with both
MIT dependencies and keeps you free to use it in commercial games. If you ever want to keep it
private/proprietary instead, MIT dependencies allow that too — you'd just drop the public MIT
grant on your own code while still retaining the third-party notices.

## Checklist if you add another gun/SDK later

- Record the new dependency's license here and in `THIRD-PARTY-NOTICES.md`.
- Prefer official docs / permissively-licensed samples as your protocol source, and never ship
  third-party application code or assets.
- If a dependency is **not** MIT/BSD/Apache (e.g. GPL, or a custom EULA), stop and check
  compatibility before taking a dependency on it — copyleft or "no redistribution" terms can
  change what you're allowed to do with the whole package.
