# Third-party notices

The MentorAgent packages contain material from the projects below. Their licenses apply to that material
only; MentorAgent's own license is in the LICENSE file, and its copyright notice in NOTICE. This file ships inside
every MentorAgent package.

## Feather icons

Some of the inline SVG icons in Novior.MentorAgent.Abstractions' Razor components are Feather icons, used as is
or redrawn as equivalent paths: microphone, paperclip, link, send, message, volume, volume off, help,
alert triangle, close and image (`ChatInput.razor`, `ChatWidget.razor`, `ConfirmationBanner.razor`).

Source: https://github.com/feathericons/feather

```
The MIT License (MIT)

Copyright (c) 2013-2023 Cole Bemis

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

## Lucide icons

The "new conversation" icon (rotate-ccw) in `ChatWidget.razor` is a Lucide icon. Lucide icons derived
from Feather remain under Feather's MIT license, above.

Source: https://github.com/lucide-icons/lucide

```
ISC License

Copyright (c) 2026 Lucide Icons and Contributors

Permission to use, copy, modify, and/or distribute this software for any
purpose with or without fee is hereby granted, provided that the above
copyright notice and this permission notice appear in all copies.

THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES
WITH REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF
MERCHANTABILITY AND FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR
ANY SPECIAL, DIRECT, INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES
WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS, WHETHER IN AN
ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION, ARISING OUT OF
OR IN CONNECTION WITH THE USE OR PERFORMANCE OF THIS SOFTWARE.
```

## Fonts included in Novior.MentorAgent.Abstractions

The Novior.MentorAgent.Abstractions package contains the Sora and JetBrains Mono fonts, which the widget's
stylesheet (`MentorAgent.css`) declares with `@font-face`: variable WOFF2 files, Latin and Latin Extended
subsets, taken from Fontsource 5.3.0 (`@fontsource-variable/sora`, `@fontsource-variable/jetbrains-mono`).
Both fonts are licensed under the SIL Open Font License 1.1. The files and the full license texts
(`OFL-Sora.txt`, `OFL-JetBrainsMono.txt`) are in `Novior.MentorAgent.Abstractions/wwwroot/fonts/`, which is
`staticwebassets/fonts/` in the package.

Sora: Copyright 2019 The Sora Project Authors (https://github.com/sora-xor/sora-font)

JetBrains Mono: Copyright 2020 The JetBrains Mono Project Authors (https://github.com/JetBrains/JetBrainsMono)
