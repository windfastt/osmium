# Third-party notices

Osmium is licensed under the MIT License (see `LICENSE`). Its own code — the Osmium CM context-mixing codec, all
filters, the `.osa` container, the planner, the cluster protocol, the command-line tool and the Windows app — is
original. Osmium CM uses techniques from the PAQ/lpaq family of compressors but contains no code from them.

The Osmium downloads also contain the third-party software listed below, used unmodified. Each entry gives the
version that ships, where it comes from, its license, and the license text. Every distribution (installer, portable
ZIP, command-line executable) carries this file as `THIRD-PARTY-NOTICES.txt`, and `osmium license` prints it.

| Component | Version | Used for | License |
|---|---|---|---|
| ZstdSharp.Port | 0.8.8 | Zstandard codec | MIT; includes code ported from Zstandard (BSD-3-Clause) |
| SharpCompress | 1.0.0 | BZip2 and PPMd codecs | MIT; includes a Bouncy Castle notice and code under Ms-PL, Apache-2.0, BSD, zlib, Info-ZIP, UnRAR and MIT licenses (see its section) |
| LZMA-SDK (NuGet package) | 22.1.1 | LZMA codec | MIT (packaging); the LZMA SDK itself is public domain |
| System.IO.Hashing | 10.0.15 | XXH3, XXH128 and CRC-32 checksums | MIT |
| .NET runtime (Microsoft.NETCore.App) | 10.0.15 | Runtime, Brotli, Deflate, AES-GCM, ECDH, HKDF | MIT, plus the notices in `dotnet-THIRD-PARTY-NOTICES.txt` |
| .NET Windows Desktop runtime (Microsoft.WindowsDesktop.App) | 10.0.15 | Windows Forms user interface | MIT, plus the Windows Forms notices below |
| Inno Setup | 6 (installer only) | Builds and runs the setup program | Inno Setup License |

---

## ZstdSharp.Port 0.8.8

https://github.com/oleg-st/ZstdSharp — a C# port of Zstandard.

```
MIT License

Copyright (c) 2021 Oleg Stepanischev

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

### Zstandard (ported into ZstdSharp.Port)

https://github.com/facebook/zstd

```
BSD License

For Zstandard software

Copyright (c) Meta Platforms, Inc. and affiliates. All rights reserved.

Redistribution and use in source and binary forms, with or without modification,
are permitted provided that the following conditions are met:

 * Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.

 * Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

 * Neither the name Facebook, nor Meta, nor the names of its contributors may
   be used to endorse or promote products derived from this software without
   specific prior written permission.

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

## SharpCompress 1.0.0

https://github.com/adamhathcock/sharpcompress — Copyright (c) 2025 Adam Hathcock (package metadata).

```
The MIT License (MIT)

Copyright (c) 2014  Adam Hathcock

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

SharpCompress credits: its XZ implementation is based on XZ.NET (https://github.com/sambott/XZ.NET) by sambott; XZ
BCJ filter support was contributed by Louis-Michel Bergeron on behalf of aDolus Technology Inc.; its 7-Zip
implementation is based on managed-lzma (https://code.google.com/p/managed-lzma/). It carries this notice:

```
Copyright (c) 2000 - 2011 The Legion Of The Bouncy Castle (http://www.bouncycastle.org)

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

The SharpCompress DLL contains code that SharpCompress took from other projects, with the notices and licenses below.
The paths are in the SharpCompress source at the commit the 1.0.0 package was built from (b6cc95a), under
`src/SharpCompress/`. Osmium itself calls only the BZip2 and PPMd codecs, but the whole library ships.

### SharpCompress: Zstandard (`Compressors/ZStandard/`)

An embedded copy of ZstdSharp (a C# port of Zstandard) with no license headers of its own. Its notices are the
ZstdSharp.Port (MIT, Copyright (c) 2021 Oleg Stepanischev) and Zstandard (BSD, Copyright (c) Meta Platforms, Inc. and
affiliates) sections above.

### SharpCompress: Deflate, zlib and GZip (`Compressors/Deflate/`)

Taken from DotNetZip (http://dotnetzip.codeplex.com), Copyright (c) 2006-2010 Dino Chiesa and Microsoft Corporation,
licensed under the Microsoft Public License (Ms-PL). DotNetZip is itself derived from jzlib (Copyright (c) 2000-2003
ymnk, JCraft, Inc., BSD) and from zlib 1.1.3 and 1.2.3 (Jean-loup Gailly and Mark Adler).

```
Microsoft Public License (Ms-PL)

This license governs use of the accompanying software. If you use the software, you
accept this license. If you do not accept the license, do not use the software.

1. Definitions
The terms "reproduce," "reproduction," "derivative works," and "distribution" have the
same meaning here as under U.S. copyright law.
A "contribution" is the original software, or any additions or changes to the software.
A "contributor" is any person that distributes its contribution under this license.
"Licensed patents" are a contributor's patent claims that read directly on its contribution.

2. Grant of Rights
(A) Copyright Grant- Subject to the terms of this license, including the license conditions
and limitations in section 3, each contributor grants you a non-exclusive, worldwide,
royalty-free copyright license to reproduce its contribution, prepare derivative works of
its contribution, and distribute its contribution or any derivative works that you create.
(B) Patent Grant- Subject to the terms of this license, including the license conditions and
limitations in section 3, each contributor grants you a non-exclusive, worldwide,
royalty-free license under its licensed patents to make, have made, use, sell, offer for
sale, import, and/or otherwise dispose of its contribution in the software or derivative
works of the contribution in the software.

3. Conditions and Limitations
(A) No Trademark License- This license does not grant you rights to use any contributors'
name, logo, or trademarks.
(B) If you bring a patent claim against any contributor over patents that you claim are
infringed by the software, your patent license from such contributor to the software ends
automatically.
(C) If you distribute any portion of the software, you must retain all copyright, patent,
trademark, and attribution notices that are present in the software.
(D) If you distribute any portion of the software in source code form, you may do so only
under this license by including a complete copy of this license with your distribution. If
you distribute any portion of the software in compiled or object code form, you may only
do so under a license that complies with this license.
(E) The software is licensed "as-is." You bear the risk of using it. The contributors give
no express warranties, guarantees, or conditions. You may have additional consumer rights
under your local laws which this license cannot change. To the extent permitted under your
local laws, the contributors exclude the implied warranties of merchantability, fitness for
a particular purpose and non-infringement.
```

jzlib license, as reproduced in the DotNetZip files:

```
Copyright (c) 2000,2001,2002,2003 ymnk, JCraft,Inc. All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice,
this list of conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright
notice, this list of conditions and the following disclaimer in
the documentation and/or other materials provided with the distribution.

3. The names of the authors may not be used to endorse or promote products
derived from this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED ``AS IS'' AND ANY EXPRESSED OR IMPLIED WARRANTIES,
INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND
FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL JCRAFT,
INC. OR ANY CONTRIBUTORS TO THIS SOFTWARE BE LIABLE FOR ANY DIRECT, INDIRECT,
INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA,
OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF
LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING
NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE,
EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

The files are also "based on zlib-1.1.3; credit to authors Jean-loup Gailly (jloup@gzip.org) and Mark Adler
(madler@alumni.caltech.edu) and contributors of zlib", and the Deflate manager on zlib 1.2.3, Copyright (C) 1995-2005
Jean-loup Gailly. The zlib license is reproduced in the next section.

### SharpCompress: Deflate64 (`Compressors/Deflate64/`)

Taken from the .NET runtime (Copyright (c) .NET Foundation and Contributors, MIT license, reproduced in the .NET runtime
section below). The inflater is derived from zlib and carries this notice:

```
zlib.h -- interface of the 'zlib' general purpose compression library
version 1.2.1, November 17th, 2003

Copyright (C) 1995-2003 Jean-loup Gailly and Mark Adler

This software is provided 'as-is', without any express or implied
warranty.  In no event will the authors be held liable for any damages
arising from the use of this software.

Permission is granted to anyone to use this software for any purpose,
including commercial applications, and to alter it and redistribute it
freely, subject to the following restrictions:

1. The origin of this software must not be misrepresented; you must not
   claim that you wrote the original software. If you use this software
   in a product, an acknowledgment in the product documentation would be
   appreciated but is not required.
2. Altered source versions must be plainly marked as such, and must not be
   misrepresented as being the original software.
3. This notice may not be removed or altered from any source distribution.
```

### SharpCompress: BZip2 (`Compressors/BZip2/`)

Copyright 2001, 2004-2005 The Apache Software Foundation, licensed under the Apache License, Version 2.0 (the source
files carry the standard license header). The code is Apache Ant's bzip2 package. The files state: "This package is
based on the work done by Keiron Liddle, Aftex Software <keiron@aftexsw.com> to whom the Ant project is very grateful
for his great code." The input stream was modified to read the leading "BZ" of the stream.

The SharpCompress source has no NOTICE file of its own. Apache Ant's NOTICE file carries these attribution notices
(its remaining paragraph concerns Ant's `<sync>` task, which is not part of this code):

```
Apache Ant
Copyright 1999-2026 The Apache Software Foundation

This product includes software developed at
The Apache Software Foundation (https://www.apache.org/).
```

The Apache License, Version 2.0:

```
                                 Apache License
                           Version 2.0, January 2004
                        http://www.apache.org/licenses/

   TERMS AND CONDITIONS FOR USE, REPRODUCTION, AND DISTRIBUTION

   1. Definitions.

      "License" shall mean the terms and conditions for use, reproduction,
      and distribution as defined by Sections 1 through 9 of this document.

      "Licensor" shall mean the copyright owner or entity authorized by
      the copyright owner that is granting the License.

      "Legal Entity" shall mean the union of the acting entity and all
      other entities that control, are controlled by, or are under common
      control with that entity. For the purposes of this definition,
      "control" means (i) the power, direct or indirect, to cause the
      direction or management of such entity, whether by contract or
      otherwise, or (ii) ownership of fifty percent (50%) or more of the
      outstanding shares, or (iii) beneficial ownership of such entity.

      "You" (or "Your") shall mean an individual or Legal Entity
      exercising permissions granted by this License.

      "Source" form shall mean the preferred form for making modifications,
      including but not limited to software source code, documentation
      source, and configuration files.

      "Object" form shall mean any form resulting from mechanical
      transformation or translation of a Source form, including but
      not limited to compiled object code, generated documentation,
      and conversions to other media types.

      "Work" shall mean the work of authorship, whether in Source or
      Object form, made available under the License, as indicated by a
      copyright notice that is included in or attached to the work
      (an example is provided in the Appendix below).

      "Derivative Works" shall mean any work, whether in Source or Object
      form, that is based on (or derived from) the Work and for which the
      editorial revisions, annotations, elaborations, or other modifications
      represent, as a whole, an original work of authorship. For the purposes
      of this License, Derivative Works shall not include works that remain
      separable from, or merely link (or bind by name) to the interfaces of,
      the Work and Derivative Works thereof.

      "Contribution" shall mean any work of authorship, including
      the original version of the Work and any modifications or additions
      to that Work or Derivative Works thereof, that is intentionally
      submitted to Licensor for inclusion in the Work by the copyright owner
      or by an individual or Legal Entity authorized to submit on behalf of
      the copyright owner. For the purposes of this definition, "submitted"
      means any form of electronic, verbal, or written communication sent
      to the Licensor or its representatives, including but not limited to
      communication on electronic mailing lists, source code control systems,
      and issue tracking systems that are managed by, or on behalf of, the
      Licensor for the purpose of discussing and improving the Work, but
      excluding communication that is conspicuously marked or otherwise
      designated in writing by the copyright owner as "Not a Contribution."

      "Contributor" shall mean Licensor and any individual or Legal Entity
      on behalf of whom a Contribution has been received by Licensor and
      subsequently incorporated within the Work.

   2. Grant of Copyright License. Subject to the terms and conditions of
      this License, each Contributor hereby grants to You a perpetual,
      worldwide, non-exclusive, no-charge, royalty-free, irrevocable
      copyright license to reproduce, prepare Derivative Works of,
      publicly display, publicly perform, sublicense, and distribute the
      Work and such Derivative Works in Source or Object form.

   3. Grant of Patent License. Subject to the terms and conditions of
      this License, each Contributor hereby grants to You a perpetual,
      worldwide, non-exclusive, no-charge, royalty-free, irrevocable
      (except as stated in this section) patent license to make, have made,
      use, offer to sell, sell, import, and otherwise transfer the Work,
      where such license applies only to those patent claims licensable
      by such Contributor that are necessarily infringed by their
      Contribution(s) alone or by combination of their Contribution(s)
      with the Work to which such Contribution(s) was submitted. If You
      institute patent litigation against any entity (including a
      cross-claim or counterclaim in a lawsuit) alleging that the Work
      or a Contribution incorporated within the Work constitutes direct
      or contributory patent infringement, then any patent licenses
      granted to You under this License for that Work shall terminate
      as of the date such litigation is filed.

   4. Redistribution. You may reproduce and distribute copies of the
      Work or Derivative Works thereof in any medium, with or without
      modifications, and in Source or Object form, provided that You
      meet the following conditions:

      (a) You must give any other recipients of the Work or
          Derivative Works a copy of this License; and

      (b) You must cause any modified files to carry prominent notices
          stating that You changed the files; and

      (c) You must retain, in the Source form of any Derivative Works
          that You distribute, all copyright, patent, trademark, and
          attribution notices from the Source form of the Work,
          excluding those notices that do not pertain to any part of
          the Derivative Works; and

      (d) If the Work includes a "NOTICE" text file as part of its
          distribution, then any Derivative Works that You distribute must
          include a readable copy of the attribution notices contained
          within such NOTICE file, excluding those notices that do not
          pertain to any part of the Derivative Works, in at least one
          of the following places: within a NOTICE text file distributed
          as part of the Derivative Works; within the Source form or
          documentation, if provided along with the Derivative Works; or,
          within a display generated by the Derivative Works, if and
          wherever such third-party notices normally appear. The contents
          of the NOTICE file are for informational purposes only and
          do not modify the License. You may add Your own attribution
          notices within Derivative Works that You distribute, alongside
          or as an addendum to the NOTICE text from the Work, provided
          that such additional attribution notices cannot be construed
          as modifying the License.

      You may add Your own copyright statement to Your modifications and
      may provide additional or different license terms and conditions
      for use, reproduction, or distribution of Your modifications, or
      for any such Derivative Works as a whole, provided Your use,
      reproduction, and distribution of the Work otherwise complies with
      the conditions stated in this License.

   5. Submission of Contributions. Unless You explicitly state otherwise,
      any Contribution intentionally submitted for inclusion in the Work
      by You to the Licensor shall be under the terms and conditions of
      this License, without any additional terms or conditions.
      Notwithstanding the above, nothing herein shall supersede or modify
      the terms of any separate license agreement you may have executed
      with Licensor regarding such Contributions.

   6. Trademarks. This License does not grant permission to use the trade
      names, trademarks, service marks, or product names of the Licensor,
      except as required for reasonable and customary use in describing the
      origin of the Work and reproducing the content of the NOTICE file.

   7. Disclaimer of Warranty. Unless required by applicable law or
      agreed to in writing, Licensor provides the Work (and each
      Contributor provides its Contributions) on an "AS IS" BASIS,
      WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or
      implied, including, without limitation, any warranties or conditions
      of TITLE, NON-INFRINGEMENT, MERCHANTABILITY, or FITNESS FOR A
      PARTICULAR PURPOSE. You are solely responsible for determining the
      appropriateness of using or redistributing the Work and assume any
      risks associated with Your exercise of permissions under this License.

   8. Limitation of Liability. In no event and under no legal theory,
      whether in tort (including negligence), contract, or otherwise,
      unless required by applicable law (such as deliberate and grossly
      negligent acts) or agreed to in writing, shall any Contributor be
      liable to You for damages, including any direct, indirect, special,
      incidental, or consequential damages of any character arising as a
      result of this License or out of the use or inability to use the
      Work (including but not limited to damages for loss of goodwill,
      work stoppage, computer failure or malfunction, or any and all
      other commercial damages or losses), even if such Contributor
      has been advised of the possibility of such damages.

   9. Accepting Warranty or Additional Liability. While redistributing
      the Work or Derivative Works thereof, You may choose to offer,
      and charge a fee for, acceptance of support, warranty, indemnity,
      or other liability obligations and/or rights consistent with this
      License. However, in accepting such obligations, You may act only
      on Your own behalf and on Your sole responsibility, not on behalf
      of any other Contributor, and only if You agree to indemnify,
      defend, and hold each Contributor harmless for any liability
      incurred by, or claims asserted against, such Contributor by reason
      of your accepting any such warranty or additional liability.

   END OF TERMS AND CONDITIONS

   APPENDIX: How to apply the Apache License to your work.

      To apply the Apache License to your work, attach the following
      boilerplate notice, with the fields enclosed by brackets "[]"
      replaced with your own identifying information. (Don't include
      the brackets!)  The text should be enclosed in the appropriate
      comment syntax for the file format. We also recommend that a
      file or class name and description of purpose be included on the
      same "printed page" as the copyright notice for easier
      identification within third-party archives.

   Copyright [yyyy] [name of copyright owner]

   Licensed under the Apache License, Version 2.0 (the "License");
   you may not use this file except in compliance with the License.
   You may obtain a copy of the License at

       http://www.apache.org/licenses/LICENSE-2.0

   Unless required by applicable law or agreed to in writing, software
   distributed under the License is distributed on an "AS IS" BASIS,
   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
   See the License for the specific language governing permissions and
   limitations under the License.
```

### SharpCompress: Adler-32 (`Algorithms/Adler32.cs`)

Copyright (c) Six Labors, licensed under the Apache License, Version 2.0 (full text above). The SIMD code is, in
the file's words, "Based on https://github.com/chromium/chromium/blob/master/third_party/zlib/adler32_simd.c"
(The Chromium Authors).

### SharpCompress: Explode (`Compressors/Explode/`)

The Zip "implode" decoder tree code "has been converted to C# based on the original huft_tree code found in inflate.c
-- by Mark Adler version c17e, 30 Mar 2007", which is Info-ZIP software. The Info-ZIP license (version 2009-Jan-02):

```
Copyright (c) 1990-2009 Info-ZIP.  All rights reserved.

For the purposes of this copyright and license, "Info-ZIP" is defined as
the following set of individuals:

   Mark Adler, John Bush, Karl Davis, Harald Denker, Jean-Michel Dubois,
   Jean-loup Gailly, Hunter Goatley, Ed Gordon, Ian Gorman, Chris Herborth,
   Dirk Haase, Greg Hartwig, Robert Heath, Jonathan Hudson, Paul Kienitz,
   David Kirschbaum, Johnny Lee, Onno van der Linden, Igor Mandrichenko,
   Steve P. Miller, Sergio Monesi, Keith Owens, George Petrov, Greg Roelofs,
   Kai Uwe Rommel, Steve Salisbury, Dave Smith, Steven M. Schweda,
   Christian Spieler, Cosmin Truta, Antoine Verheijen, Paul von Behren,
   Rich Wales, Mike White.

This software is provided "as is," without warranty of any kind, express
or implied.  In no event shall Info-ZIP or its contributors be held liable
for any direct, indirect, incidental, special or consequential damages
arising out of the use of or inability to use this software.

Permission is granted to anyone to use this software for any purpose,
including commercial applications, and to alter it and redistribute it
freely, subject to the above disclaimer and the following restrictions:

    1. Redistributions of source code (in whole or in part) must retain
       the above copyright notice, definition, disclaimer, and this list
       of conditions.

    2. Redistributions in binary form (compiled executables and libraries)
       must reproduce the above copyright notice, definition, disclaimer,
       and this list of conditions in documentation and/or other materials
       provided with the distribution.  Additional documentation is not needed
       for executables where a command line license option provides these and
       a note regarding this option is in the executable's startup banner.  The
       sole exception to this condition is redistribution of a standard
       UnZipSFX binary (including SFXWiz) as part of a self-extracting archive;
       that is permitted without inclusion of this license, as long as the
       normal SFX banner has not been removed from the binary or disabled.

    3. Altered versions--including, but not limited to, ports to new operating
       systems, existing ports with new graphical interfaces, versions with
       modified or added functionality, and dynamic, shared, or static library
       versions not from Info-ZIP--must be plainly marked as such and must not
       be misrepresented as being the original source or, if binaries,
       compiled from the original source.  Such altered versions also must not
       be misrepresented as being Info-ZIP releases--including, but not
       limited to, labeling of the altered versions with the names "Info-ZIP"
       (or any variation thereof, including, but not limited to, different
       capitalizations), "Pocket UnZip," "WiZ" or "MacZip" without the
       explicit permission of Info-ZIP.  Such altered versions are further
       prohibited from misrepresentative use of the Zip-Bugs or Info-ZIP
       e-mail addresses or the Info-ZIP URL(s), such as to imply Info-ZIP
       will provide support for the altered versions.

    4. Info-ZIP retains the right to use the names "Info-ZIP," "Zip," "UnZip,"
       "UnZipSFX," "WiZ," "Pocket UnZip," "Pocket Zip," and "MacZip" for its
       own source and binary releases.
```

### SharpCompress: RAR decoders (`Compressors/Rar/`, `Compressors/PPMd/H/`)

SharpCompress decompresses RAR with code derived from the UnRAR source code by Alexander Roshal. `UnpackV1/`,
`VM/` and `PPMd/H/` come from junrar, the Java port of UnRAR (`UnpackV1/Unpack15.cs` and `Unpack20.cs` carry
"Copyright (c) 2007 innoSysTec (R) GmbH, Germany. All rights reserved. Original author: Edmund Wagner", and state "the
unrar licence applies to all junrar source and binary distributions; you are not allowed to use this source to
re-create the RAR compression algorithm"). `UnpackV2017/` is a port of the C++ UnRAR 5.x decompressor. The RAR text
model is based on Dmitry Shkarin's PPMII (see below). The UnRAR license, as shipped in `reference/unrar/license.txt` of the
SharpCompress source, applies to all of it. Osmium does not use this code to create RAR archives and cannot do so.

```
 ******    *****   ******   UnRAR - free utility for RAR archives
 **   **  **   **  **   **  ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
 ******   *******  ******    License for use and distribution of
 **   **  **   **  **   **   ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
 **   **  **   **  **   **         FREE portable version
                                   ~~~~~~~~~~~~~~~~~~~~~

      The source code of UnRAR utility is freeware. This means:

   1. All copyrights to RAR and the utility UnRAR are exclusively
      owned by the author - Alexander Roshal.

   2. UnRAR source code may be used in any software to handle
      RAR archives without limitations free of charge, but cannot be
      used to develop RAR (WinRAR) compatible archiver and to
      re-create RAR compression algorithm, which is proprietary.
      Distribution of modified UnRAR source code in separate form
      or as a part of other software is permitted, provided that
      full text of this paragraph, starting from "UnRAR source code"
      words, is included in license, or in documentation if license
      is not available, and in source code comments of resulting package.

   3. The UnRAR utility may be freely distributed. It is allowed
      to distribute UnRAR inside of other software packages.

   4. THE RAR ARCHIVER AND THE UnRAR UTILITY ARE DISTRIBUTED "AS IS".
      NO WARRANTY OF ANY KIND IS EXPRESSED OR IMPLIED.  YOU USE AT 
      YOUR OWN RISK. THE AUTHOR WILL NOT BE LIABLE FOR DATA LOSS, 
      DAMAGES, LOSS OF PROFITS OR ANY OTHER KIND OF LOSS WHILE USING
      OR MISUSING THIS SOFTWARE.

   5. Installing and using the UnRAR utility signifies acceptance of
      these terms and conditions of the license.

   6. If you don't agree with terms of the license you must remove
      UnRAR files from your storage devices and cease to use the
      utility.

      Thank you for your interest in RAR and UnRAR.


                                            Alexander L. Roshal
```

### SharpCompress: PPMd (`Compressors/PPMd/I1/`, `Compressors/PPMd/H/`)

`I1/Model.cs`: "This is a port of Dmitry Shkarin's PPMd Variant I Revision 1. Ported by Michael Bone
(mjbone03@yahoo.com.au)." This is the PPMd codec Osmium uses for 7z/Zip PPMd. `H/` (PPMd variant H, used inside RAR) is
Dmitry Shkarin's PPMd, through junrar and UnRAR as described in the previous section; UnRAR's acknowledgments
describe the RAR text algorithm as "based on Dmitry Shkarin PPMII and Dmitry Subbotin carryless rangecoder public domain
source code". Shkarin's PPMd source is public domain.

### SharpCompress: LZW (`Compressors/Lzw/LzwStream.cs`)

The `.Z` (compress) decoder is a copy of SharpZipLib's `LzwInputStream` (`ICSharpCode.SharpZipLib.Lzw`, whose name its
documentation still uses), which is "Based on Java code by Ronald Tschalar, which in turn was based on the unlzw.c code
in the gzip package". The SharpCompress file carries no license text. SharpZipLib's license:

```
Copyright © 2000-2018 SharpZipLib Contributors

Permission is hereby granted, free of charge, to any person obtaining a copy of this
software and associated documentation files (the "Software"), to deal in the Software
without restriction, including without limitation the rights to use, copy, modify, merge,
publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or
substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
DEALINGS IN THE SOFTWARE.
```

### SharpCompress: ADC (`Compressors/ADC/`)

Author Natalia Portillo <claunia@claunia.com>, Copyright (c) 2016 © Claunia.com, MIT license:

```
MIT License

Copyright (c) 2016 © Claunia.com

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

### SharpCompress: other credits

LZMA, LZMA2 and the 7-Zip range coder in `Compressors/LZMA/` follow Igor Pavlov's LZMA SDK (public domain, see the
LZMA-SDK section below) through managed-lzma. The XZ and 7-Zip credits are in the paragraph above.

## LZMA-SDK 22.1.1 (NuGet package)

https://github.com/monemihir/LZMA-SDK — the C# part of the LZMA SDK by Igor Pavlov (https://www.7-zip.org/sdk.html),
packaged for NuGet by Mihir Mone. The packaging is licensed as follows (the package metadata says "(c) 2023 Mihir
Mone"):

```
MIT License

Copyright (c) 2020 Mihir Mone

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

The LZMA SDK itself is in the public domain. From https://www.7-zip.org/sdk.html: "LZMA SDK is placed in the public
domain. Anyone is free to copy, modify, publish, use, compile, sell, or distribute the original LZMA SDK code, either
in source code form or as a compiled binary, for any purpose, commercial or non-commercial, and by any means."

## System.IO.Hashing 10.0.15

https://github.com/dotnet/runtime — part of .NET. Licensed under the .NET MIT license reproduced in the next
section. Its third-party notices (which include xxHash) are the .NET runtime notices shipped as
`dotnet-THIRD-PARTY-NOTICES.txt`.

## .NET runtime 10.0.15 (Microsoft.NETCore.App)

https://github.com/dotnet/runtime — the self-contained downloads include the .NET runtime. Its license is shipped as
`dotnet-LICENSE.txt` and its third-party notices (zlib-ng, Brotli, MsQuic, xxHash and others) as
`dotnet-THIRD-PARTY-NOTICES.txt`.

```
The MIT License (MIT)

Copyright (c) .NET Foundation and Contributors

All rights reserved.

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

## .NET Windows Desktop runtime 10.0.15 (Microsoft.WindowsDesktop.App)

https://github.com/dotnet/winforms and https://github.com/dotnet/wpf — the Windows app uses Windows Forms. Licensed
under the same .NET MIT license as above.

Windows Forms third-party notices (from https://github.com/dotnet/winforms/blob/main/THIRD-PARTY-NOTICES.TXT):

```
.NET Core uses third-party libraries or other resources that may be
distributed under licenses different than the .NET Core software.

In the event that we accidentally failed to list a required notice, please
bring it to our attention. Post an issue or email us:

           dotnet@microsoft.com

The attached notices are provided for information only.

License notice for Ookie.Dialogs
--------------------------------

http://www.ookii.org/software/dialogs/

Copyright © Sven Groot (Ookii.org) 2009
All rights reserved.


Redistribution and use in source and binary forms, with or without 
modification, are permitted provided that the following conditions are met:

1) Redistributions of source code must retain the above copyright notice, 
   this list of conditions and the following disclaimer. 
2) Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution. 
3) Neither the name of the ORGANIZATION nor the names of its contributors
   may be used to endorse or promote products derived from this software
   without specific prior written permission. 

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE
LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR
CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF
SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS
INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN
CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF
THE POSSIBILITY OF SUCH DAMAGE.
```

## Inno Setup (installer only)

https://jrsoftware.org/isinfo.php — the Windows installer is built with Inno Setup, and the setup program contains
Inno Setup's runtime code.

```
Inno Setup License
==================

Except where otherwise noted, all of the documentation and software included in the Inno
Setup package is copyrighted by Jordan Russell.

Copyright (C) 1997-2026 Jordan Russell. All rights reserved.
Portions Copyright (C) 2000-2026 Martijn Laan. All rights reserved.

This software is provided "as-is," without any express or implied warranty. In no event shall
the author be held liable for any damages arising from the use of this software.

Permission is granted to anyone to use this software for any purpose, including commercial
applications, and to alter and redistribute it, provided that the following conditions are met:

1. All redistributions of source code files must retain all copyright notices that are currently
   in place, and this list of conditions without modification.

2. All redistributions in binary form must retain all occurrences of the above copyright notice
   and web site addresses that are currently in place (for example, in the About boxes).

3. The origin of this software must not be misrepresented; you must not claim that you wrote
   the original software. If you use this software to distribute a product, an acknowledgment
   in the product documentation would be appreciated but is not required.

4. Modified versions in source or binary form must be plainly marked as such, and must not
   be misrepresented as being the original software.


Jordan Russell
jr-2020 AT jrsoftware.org
https://jrsoftware.org/
```

---

The fonts used on the project website (Anybody, Instrument Sans, Martian Mono) are not part of the downloads; they
are licensed under the SIL Open Font License 1.1, with their license files next to them in `docs/fonts/`.
