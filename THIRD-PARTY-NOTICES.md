# Third-Party Notices

Palmware Lightguns builds on, and derives protocol knowledge from, the following
third-party works. Both are distributed under the MIT License, which permits use,
modification, and redistribution provided the copyright and permission notice are
retained. Those notices are reproduced below.

Palmware Lightguns does **not** bundle either project — it depends on the Blamcon
package being installed separately, and the Retro Shooter code below was written
from a reference example, not copied wholesale. These notices are included for
attribution and to document provenance.

---

## 1. Blamcon Lightguns for Unity (`com.blamcon.lightguns`)

- Author: Props3D
- Source: https://github.com/Props3D/UnityLightguns
- Relationship: **runtime dependency.** `Palmware.Lightguns.Blamcon` references this
  package's `Blamcon.Lightguns` assembly (device layout, HID force-feedback commands,
  position remap processor). The Blamcon package is installed separately by the user;
  its code is not redistributed here.

```
The MIT License (MIT)

Copyright (c) 2025 Props3D

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
```

---

## 2. Retro Shooter Example Game

- Author: Grimbar Interactive LLC
- Source: Retro Shooter RS3 "Reaper" example Unity project (MIT).
- Relationship: **derived protocol.** The RS3 serial command set used in
  `RetroShooterModule` (`ZS`/`ZX`/`ZJ`/`ZM`/`Z0`-`Z5`/`ZZ`/`Z6`, COM port at
  115200 8-N-1) is taken from this example's `LightgunController.cs`. The Palmware
  implementation was rewritten into the module architecture, but the wire protocol
  and constants originate here.

```
MIT License

Copyright (c) 2025 Grimbar Interactive LLC

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

---

## 3. Blamcon serial protocol

The Blamcon serial configuration/feedback commands (`ZD.*`, `ZY.*`, `F.*`) used in
`BlamconSerial` are Blamcon's **publicly documented** serial protocol:
https://blamcon.com/get-started-with-blamcon/serial-commands/. These are functional
interoperability facts (command strings and byte formats), used here to communicate
with Blamcon hardware the user owns. See `Documentation~/licensing.md` for the full
reasoning.
