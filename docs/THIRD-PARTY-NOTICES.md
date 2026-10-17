# 第三方组件与许可证 / Third-Party Components and Licenses

本项目使用或分发若干第三方组件，其许可可能不同于本项目自身的许可。本项目本身以 GPL-3.0 发布，完整条款见 `LICENSE`。若发现本文档遗漏了应当列出的声明，请到 [GitHub Issues](https://github.com/Shiina-Tak1/BullBull-Unpacker/issues) 提出。以下声明仅供参考。

This project uses or distributes third-party components that may be distributed under licenses different from the project's own. The project itself is released under GPL-3.0; the complete terms are in `LICENSE`. In the event that we accidentally failed to list a required notice, please bring it to our attention at [GitHub Issues](https://github.com/Shiina-Tak1/BullBull-Unpacker/issues). The notices below are provided for information only.

## UnRAR 许可声明 / License notice for UnRAR

**版本：** 7.30 beta 1。

**许可：** Freeware（允许免费分发，包括随其他软件包分发）。

**版权：** All copyrights to RAR and the utility UnRAR are exclusively owned by the author - Alexander Roshal.

**许可原文：** 见 `src\Bbu.App\engines\unrar\license.txt`，本文档不重复。

**Version:** 7.30 beta 1.

**License:** Freeware (redistribution is permitted, including distribution as part of other software packages).

**Copyright:** All copyrights to RAR and the utility UnRAR are exclusively owned by the author - Alexander Roshal.

**License text:** See `src\Bbu.App\engines\unrar\license.txt`; not repeated here.

## LZMA2 解码移植许可声明 / License notice for LZMA2 Decoder Port

**许可：** 公有领域（LZMA SDK）。

**版权：** Igor Pavlov。

**许可原文：** 见 `C/LzmaDec.c` 与 `C/Lzma2Dec.c` 的源码头（`Igor Pavlov : Public domain`），另见 LZMA SDK 官方页面 <https://7-zip.org/sdk.html>。

**License:** Public domain (LZMA SDK).

**Copyright:** Igor Pavlov.

**License text:** See the source headers of `C/LzmaDec.c` and `C/Lzma2Dec.c` (`Igor Pavlov : Public domain`); see also the official LZMA SDK page <https://7-zip.org/sdk.html>.

## 7-Zip ZS 许可声明 / License notice for 7-Zip ZS

**版本：** 26.02 ZS v1.5.7 R1。

**许可：** `7z.exe` 与 `7z.dll` 主体均为 GNU LGPL 2.1 或更高版本；`7z.dll` 中另有部分代码为 GNU LGPL 2.1 附 unRAR 许可限制、BSD 3-clause 或 BSD 2-clause。

**版权：** 7-Zip Copyright (C) 1999-2026 Igor Pavlov. BSD 3-clause 部分：Copyright (c) 2015-2016, Apple Inc. All rights reserved.; Copyright (c) Facebook, Inc. All rights reserved.; Copyright (c) 2023-2026 Igor Pavlov. BSD 2-clause 部分：Copyright (c) 2012-2021 Yann Collet.; Copyright (c) 2023-2026 Igor Pavlov. unRAR 部分：All copyrights to original unRAR code are owned by Alexander Roshal.

**许可原文：** 见 `src\Bbu.App\engines\7z\License.txt`，本文档不重复。该文件登记的是随 7-Zip 官方代码一同发布的许可；`7z.dll` 中另有下列编解码器，它们的许可未登记在该文件内，因此各自列出。

**Version:** 26.02 ZS v1.5.7 R1.

**License:** GNU LGPL 2.1 or later for `7z.exe` and for most of `7z.dll`; some code in `7z.dll` is under GNU LGPL 2.1 with unRAR license restriction, BSD 3-clause, or BSD 2-clause.

**Copyright:** 7-Zip Copyright (C) 1999-2026 Igor Pavlov. BSD 3-clause parts: Copyright (c) 2015-2016, Apple Inc. All rights reserved.; Copyright (c) Facebook, Inc. All rights reserved.; Copyright (c) 2023-2026 Igor Pavlov. BSD 2-clause parts: Copyright (c) 2012-2021 Yann Collet.; Copyright (c) 2023-2026 Igor Pavlov. unRAR parts: all copyrights to the original unRAR code are owned by Alexander Roshal.

**License text:** See `src\Bbu.App\engines\7z\License.txt`; not repeated here. That file registers the licenses shipped with the official 7-Zip code. The codecs listed below are also present in `7z.dll` but are not registered there, so they are listed individually.

## LZ4 许可声明 / License notice for LZ4

**许可：** BSD 2-Clause。

**版权：** Copyright (c) 2011-2020, Yann Collet. All rights reserved.

**许可原文：** 见下，取自 7-Zip ZS 源码树的 `C/lz4/LICENSE`（上游为 <https://github.com/lz4/lz4> 的 `lib/LICENSE`）。

**License:** BSD 2-Clause.

**Copyright:** Copyright (c) 2011-2020, Yann Collet. All rights reserved.

**License text:** Reproduced below, from `C/lz4/LICENSE` in the 7-Zip ZS source tree (upstream: `lib/LICENSE` in <https://github.com/lz4/lz4>).

```text
LZ4 Library
Copyright (c) 2011-2020, Yann Collet
All rights reserved.

Redistribution and use in source and binary forms, with or without modification,
are permitted provided that the following conditions are met:

* Redistributions of source code must retain the above copyright notice, this
  list of conditions and the following disclaimer.

* Redistributions in binary form must reproduce the above copyright notice, this
  list of conditions and the following disclaimer in the documentation and/or
  other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR
ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON
ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

## LZ5 许可声明 / License notice for LZ5

**许可：** BSD 2-Clause。

**版权：** Copyright (c) 2011-2015, Yann Collet. All rights reserved.

**许可原文：** 见下，取自 7-Zip ZS 源码树的 `C/lz5/LICENSE`（上游为 <https://github.com/inikep/lz5>）。该文件的标题沿用了上游的 "LZ4 Library"，因为 LZ5 自 LZ4 派生；标题与版权年份均按原文保留。

**License:** BSD 2-Clause.

**Copyright:** Copyright (c) 2011-2015, Yann Collet. All rights reserved.

**License text:** Reproduced below, from `C/lz5/LICENSE` in the 7-Zip ZS source tree (upstream: <https://github.com/inikep/lz5>). The heading follows the upstream "LZ4 Library" title, since LZ5 derives from LZ4; the heading and the copyright years are kept as-is.

```text
LZ4 Library
Copyright (c) 2011-2015, Yann Collet
All rights reserved.

Redistribution and use in source and binary forms, with or without modification,
are permitted provided that the following conditions are met:

* Redistributions of source code must retain the above copyright notice, this
  list of conditions and the following disclaimer.

* Redistributions in binary form must reproduce the above copyright notice, this
  list of conditions and the following disclaimer in the documentation and/or
  other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR
ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON
ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

## Lizard 许可声明 / License notice for Lizard

**许可：** BSD 2-Clause。

**版权：** Copyright (C) 2011-2016, Yann Collet. Copyright (C) 2016-2017, Przemyslaw Skibinski <inikep@gmail.com>. All rights reserved.

**许可原文：** 见下，取自 7-Zip ZS 源码树的 `C/lizard/LICENSE`（上游为 <https://github.com/inikep/lizard> 的 `lib/LICENSE`）。

**License:** BSD 2-Clause.

**Copyright:** Copyright (C) 2011-2016, Yann Collet. Copyright (C) 2016-2017, Przemyslaw Skibinski <inikep@gmail.com>. All rights reserved.

**License text:** Reproduced below, from `C/lizard/LICENSE` in the 7-Zip ZS source tree (upstream: `lib/LICENSE` in <https://github.com/inikep/lizard>).

```text
Lizard Library
Copyright (C) 2011-2016, Yann Collet.
Copyright (C) 2016-2017, Przemyslaw Skibinski <inikep@gmail.com>
All rights reserved.

Redistribution and use in source and binary forms, with or without modification,
are permitted provided that the following conditions are met:

* Redistributions of source code must retain the above copyright notice, this
  list of conditions and the following disclaimer.

* Redistributions in binary form must reproduce the above copyright notice, this
  list of conditions and the following disclaimer in the documentation and/or
  other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR
ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON
ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

## Brotli 许可声明 / License notice for Brotli

**许可：** MIT。

**版权：** Copyright (c) 2009, 2010, 2013-2016 by the Brotli Authors.

**许可原文：** 见下，取自 7-Zip ZS 源码树的 `C/brotli/LICENSE`（上游为 <https://github.com/google/brotli> 的 `LICENSE`）。

**License:** MIT.

**Copyright:** Copyright (c) 2009, 2010, 2013-2016 by the Brotli Authors.

**License text:** Reproduced below, from `C/brotli/LICENSE` in the 7-Zip ZS source tree (upstream: `LICENSE` in <https://github.com/google/brotli>).

```text
Copyright (c) 2009, 2010, 2013-2016 by the Brotli Authors.

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
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.  IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
```

## Fast-LZMA2 许可声明 / License notice for Fast-LZMA2

**许可：** BSD 3-Clause。

**版权：** Copyright 2018-present Conor McCarthy.

**许可原文：** 见下，取自 7-Zip ZS 源码树的 `C/fast-lzma2/LICENSE`（上游为 <https://github.com/conor42/fast-lzma2> 的 `LICENSE`）。原文的条款排布为长行，此处按原文保留。

**License:** BSD 3-Clause.

**Copyright:** Copyright 2018-present Conor McCarthy.

**License text:** Reproduced below, from `C/fast-lzma2/LICENSE` in the 7-Zip ZS source tree (upstream: `LICENSE` in <https://github.com/conor42/fast-lzma2>). The clauses are laid out as long lines upstream; they are kept as-is.

```text
Copyright 2018-present Conor McCarthy

Redistribution and use in source and binary forms, with or without modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this list of conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following disclaimer in the documentation and/or other materials provided with the distribution.

3. Neither the name of the copyright holder nor the names of its contributors may be used to endorse or promote products derived from this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

## Avalonia 许可声明 / License notice for Avalonia

**许可：** MIT。

**版权：** Copyright (c) AvaloniaUI OÜ. All Rights Reserved.

**许可原文：** 见下，取自上游 <https://github.com/AvaloniaUI/Avalonia> 的 `licence.md`（版本 11.3.22；该 NuGet 包未附许可文件）。

**License:** MIT.

**Copyright:** Copyright (c) AvaloniaUI OÜ. All Rights Reserved.

**License text:** Reproduced below, from `licence.md` in <https://github.com/AvaloniaUI/Avalonia> (version 11.3.22; the NuGet package carries no license file).

```text
The MIT License (MIT)

Copyright (c) AvaloniaUI OÜ
All Rights Reserved

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

## SkiaSharp 许可声明 / License notice for SkiaSharp

**许可：** MIT。

**版权：** Copyright (c) 2015-2016 Xamarin, Inc. Copyright (c) 2017-2018 Microsoft Corporation.

**许可原文：** 见下，取自 NuGet 包 `SkiaSharp.NativeAssets.Win32` 2.88.9 内的 `LICENSE.txt`；其内嵌的第三方声明见 `docs\THIRD-PARTY-NOTICES-SkiaSharp.txt`，本文档不重复。

**License:** MIT.

**Copyright:** Copyright (c) 2015-2016 Xamarin, Inc. Copyright (c) 2017-2018 Microsoft Corporation.

**License text:** Reproduced below, from `LICENSE.txt` in the NuGet package `SkiaSharp.NativeAssets.Win32` 2.88.9; the third-party notices embedded in the library are in `docs\THIRD-PARTY-NOTICES-SkiaSharp.txt`; not repeated here.

```text
Copyright (c) 2015-2016 Xamarin, Inc.
Copyright (c) 2017-2018 Microsoft Corporation.

Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the "Software"), to deal in
the Software without restriction, including without limitation the rights to
use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of
the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS
FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER
IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

## HarfBuzzSharp 许可声明 / License notice for HarfBuzzSharp

**许可：** MIT。

**版权：** Copyright (c) 2015-2016 Xamarin, Inc. Copyright (c) 2017-2018 Microsoft Corporation.

**许可原文：** 见下，取自 NuGet 包 `HarfBuzzSharp.NativeAssets.Win32` 8.3.1.1 内的 `LICENSE.txt`；其内嵌的第三方声明见 `docs\THIRD-PARTY-NOTICES-SkiaSharp.txt`（与 SkiaSharp 共用同一份），本文档不重复。

**License:** MIT.

**Copyright:** Copyright (c) 2015-2016 Xamarin, Inc. Copyright (c) 2017-2018 Microsoft Corporation.

**License text:** Reproduced below, from `LICENSE.txt` in the NuGet package `HarfBuzzSharp.NativeAssets.Win32` 8.3.1.1; the third-party notices embedded in the library are in `docs\THIRD-PARTY-NOTICES-SkiaSharp.txt` (shared with SkiaSharp); not repeated here.

```text
Copyright (c) 2015-2016 Xamarin, Inc.
Copyright (c) 2017-2018 Microsoft Corporation.

Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the "Software"), to deal in
the Software without restriction, including without limitation the rights to
use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of
the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS
FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER
IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

## ANGLE 许可声明 / License notice for ANGLE

**许可：** BSD 3-Clause。

**版权：** Copyright 2018 The ANGLE Project Authors. All rights reserved.

**许可原文：** 见下，取自 NuGet 包 `Avalonia.Angle.Windows.Natives` 2.1.25547.20250602 内的 `LICENSE`（`runtime\av_libglesv2.dll` 由该包提供）。原文每行带 `//` 注释符，此处按原文保留。

**License:** BSD 3-Clause.

**Copyright:** Copyright 2018 The ANGLE Project Authors. All rights reserved.

**License text:** Reproduced below, from `LICENSE` in the NuGet package `Avalonia.Angle.Windows.Natives` 2.1.25547.20250602, which provides `runtime\av_libglesv2.dll`. Every line carries a `//` comment marker upstream; they are kept as-is.

```text
// Copyright 2018 The ANGLE Project Authors.
// All rights reserved.
//
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions
// are met:
//
//     Redistributions of source code must retain the above copyright
//     notice, this list of conditions and the following disclaimer.
//
//     Redistributions in binary form must reproduce the above
//     copyright notice, this list of conditions and the following
//     disclaimer in the documentation and/or other materials provided
//     with the distribution.
//
//     Neither the name of TransGaming Inc., Google Inc., 3DLabs Inc.
//     Ltd., nor the names of their contributors may be used to endorse
//     or promote products derived from this software without specific
//     prior written permission.
//
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
// "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
// LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS
// FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE
// COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT,
// INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING,
// BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
// LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
// CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT
// LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN
// ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
// POSSIBILITY OF SUCH DAMAGE.
```

## Lucide 图标许可声明 / License notice for Lucide Icons

**许可：** ISC；其中派生自 Feather 项目的图标为 MIT。

**版权：** Copyright (c) 2026 Lucide Icons and Contributors。Feather 派生图标部分：Copyright (c) 2013-present Cole Bemis。

**许可原文：** 见下两段，取自上游 <https://github.com/lucide-icons/lucide> 的 `LICENSE`（版本 1.52.0）：ISC 段，以及其后另列的 Feather 派生图标名单所适用的 MIT 段。

**License:** ISC; the icons derived from the Feather project are under MIT.

**Copyright:** Copyright (c) 2026 Lucide Icons and Contributors. Feather-derived icons: Copyright (c) 2013-present Cole Bemis.

**License text:** Both texts are reproduced below, from `LICENSE` in <https://github.com/lucide-icons/lucide> (version 1.52.0): the ISC section, and the MIT section applicable to the Feather-derived icon list that follows it.

**ISC 许可原文 / ISC license text：**

```text
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

**Feather 派生图标许可原文（MIT）/ Feather-derived icons license text (MIT)：**

```text
The MIT License (MIT) (for the icons listed above)

Copyright (c) 2013-present Cole Bemis

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
