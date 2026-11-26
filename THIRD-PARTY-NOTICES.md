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

## YamlDotNet, a dependency of Novior.MentorAgent.Declarative

Novior.MentorAgent.Declarative reads the team definition files (`*.team.yaml`) with YamlDotNet 18.1.0. YamlDotNet is
not included in the package: NuGet installs it as a dependency, under its own license, the MIT License.

YamlDotNet: Copyright (c) Antoine Aubry and contributors (https://github.com/aaubry/YamlDotNet)

## Fonts included in Novior.MentorAgent.Abstractions

The Novior.MentorAgent.Abstractions package contains the Sora and JetBrains Mono fonts, which the widget's
stylesheet (`MentorAgent.css`) declares with `@font-face`: variable WOFF2 files, Latin and Latin Extended
subsets, taken from Fontsource 5.3.0 (`@fontsource-variable/sora`, `@fontsource-variable/jetbrains-mono`).
Both fonts are licensed under the SIL Open Font License 1.1. The files and the full license texts
(`OFL-Sora.txt`, `OFL-JetBrainsMono.txt`) are in `Novior.MentorAgent.Abstractions/wwwroot/fonts/`, which is
`staticwebassets/fonts/` in the package.

Sora: Copyright 2019 The Sora Project Authors (https://github.com/sora-xor/sora-font)

JetBrains Mono: Copyright 2020 The JetBrains Mono Project Authors (https://github.com/JetBrains/JetBrainsMono)

## Test only, not shipped: React and the SignalR client in Novior.MentorAgent.Tests

The test project runs a real React front end in a browser with files copied unchanged from npm: React 19.2.7,
React DOM 19.2.7 and Scheduler 0.27.0 (MIT License, Copyright (c) Meta Platforms, Inc. and affiliates), and
`@microsoft/signalr` 10.0.0 (MIT License, Copyright (c) .NET Foundation and Contributors). They are in
`Novior.MentorAgent.Tests/FrontEnd/` with their license texts (`LICENSE-react.txt`, `LICENSE-signalr.txt`) and
`SOURCE.txt`. The test project is not packed: no package contains them.

It also runs a real Angular front end: `angular.main.js`, built by the Angular CLI 22.2.4 from `FrontEnd/angular/`,
bundles parts of Angular 22.2.4 (`@angular/core`, `common`, `forms`, `platform-browser`; MIT License, Copyright (c)
2010-2026 Google LLC), RxJS 7.8.2 (Apache License 2.0) and tslib 2.9.1 (0BSD). Their license texts, as the build
extracted them, are in `LICENSE-angular.txt`.

It measures the widget's accessibility with axe-core 4.10.3 (Mozilla Public License 2.0, Copyright (c) 2015 - 2025
Deque Systems, Inc.): `axe.min.js`, copied unchanged from the npm package, with its license text
(`LICENSE-axe-core.txt`). It is loaded into the test page only while a test runs.
