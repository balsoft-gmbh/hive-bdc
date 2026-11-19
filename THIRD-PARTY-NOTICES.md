# Third-party notices

Hive BDC, by Balsoft contains the third-party components below. They are
used under their own licences, which this file reproduces. Nothing in the
Hive BDC licence (LICENSE, EULA.md) restricts the rights these licences
grant for these components.

## Components shipped with Hive BDC

| Component | Version | Used for | Copyright | Licence |
|---|---|---|---|---|
| Balsoft.Hive.EInvoice, Balsoft.Hive.EInvoice.Pdf | 1.1.0 | EN 16931 e-invoice model, XRechnung and ZUGFeRD writer | Copyright (c) Balsoft GmbH | Apache-2.0 |
| Balsoft.Hive.Installer.Windows | 1.1.0 | Setup.exe and the uninstaller | Copyright (c) Balsoft GmbH | Apache-2.0 |
| PDFsharp | 6.2.4 | PDF/A-3 handling for ZUGFeRD, Word to PDF rendering | Copyright (c) empira Software GmbH | MIT |
| DocumentFormat.OpenXml, DocumentFormat.OpenXml.Framework | 3.5.1 | reading Word invoices (.docx) and writing the Word template | Copyright (c) Microsoft Corporation | MIT |
| Serilog, Serilog.Extensions.Hosting, Serilog.Extensions.Logging, Serilog.Sinks.Console, Serilog.Sinks.File | 4.3.0, 10.0.0, 10.0.0, 6.1.1, 7.0.0 | logging | Copyright (c) Serilog Contributors | Apache-2.0 |
| Microsoft.Data.Sqlite, Microsoft.Data.Sqlite.Core | 10.0.10 | export ledger | Copyright (c) .NET Foundation and Contributors | MIT |
| SQLitePCLRaw (bundle_e_sqlite3, core, lib.e_sqlite3, provider.e_sqlite3) | 2.1.12 | SQLite binding | Copyright 2014-2024 SourceGear, LLC | Apache-2.0 |
| SQLite (native library e_sqlite3) | bundled with SQLitePCLRaw 2.1.12 | database engine | none (public domain) | Public domain |
| Microsoft.Extensions.* (Hosting, Hosting.WindowsServices, Configuration, DependencyInjection, Logging, Options and their dependencies) | 10.0.9 | service host, configuration, logging | Copyright (c) .NET Foundation and Contributors | MIT |
| System.Security.Cryptography.Pkcs, System.Security.Cryptography.ProtectedData, System.ServiceProcess.ServiceController, System.Diagnostics.EventLog | 8.0.2, 10.0.0, 10.0.x, 10.0.9 | installer signature check, encrypted settings, service control, event log | Copyright (c) .NET Foundation and Contributors | MIT |
| CommunityToolkit.Mvvm | 8.4.0 | dashboard view models | Copyright (c) .NET Foundation and Contributors | MIT |
| Azure.Storage.Blobs, Azure.Data.Tables, Azure.Messaging.ServiceBus, Azure.Identity, Azure.Security.KeyVault.Secrets (with Azure.Core and their dependencies) | 12.30.1, 12.13.0, 7.21.0, 1.21.0, 4.12.0 | Azure Blob Storage source and target, Azure run mode | Copyright (c) Microsoft Corporation | MIT |
| .NET runtime (bundled, self-contained build) | 10.0 | runs the service and the dashboard | Copyright (c) .NET Foundation and Contributors | MIT, plus the runtime's own third-party notices: https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT |
| ASP.NET Core runtime (bundled, self-contained build) | 10.0 | the service's local API (Kestrel) | Copyright (c) .NET Foundation and Contributors | MIT, plus its own third-party notices: https://github.com/dotnet/aspnetcore/blob/main/THIRD-PARTY-NOTICES.txt |
| Next.js (client runtime of the static export) | 16.4.5 | Azure web console | Copyright (c) 2025 Vercel, Inc. | MIT |
| React, React DOM, scheduler | 19.3.0, 19.3.0, 0.28.0 | Azure web console | Copyright (c) Meta Platforms, Inc. and affiliates | MIT |
| Fluent UI React Components (@fluentui/react-components with tabster, keyborg, @griffel and @floating-ui) | 9.74.7 | Azure web console controls | Copyright (c) Microsoft Corporation; @floating-ui: Copyright (c) 2021-present Floating UI contributors | MIT |
| lucide-react | 0.577.0 | icons of the Azure web console; the Windows app draws the same icons from their path data | Copyright (c) Lucide Contributors; portions Copyright (c) 2013-2026 Cole Bemis (Feather, MIT) | ISC |
| DM Sans | variable in the web console; in the Windows app static files per weight cut from it (`build/make-static-fonts.py`) | text font of the Azure web console and the Windows app | Copyright 2014 The DM Sans Project Authors | SIL Open Font License 1.1 |
| Manrope | variable in the web console; in the Windows app static files per weight cut from it (`build/make-static-fonts.py`) | heading font of the Azure web console and the Windows app | Copyright 2018 The Manrope Project Authors | SIL Open Font License 1.1 |
| Liberation Sans, Liberation Serif, Liberation Mono | 2.1.5 (liberation-fonts-ttf-2.1.5.tar.gz, github.com/liberationfonts/liberation-fonts) | Word to PDF where Arial, Times New Roman or Courier New is not installed | Digitized data copyright (c) 2010 Google Corporation with Reserved Font Arimo, Tinos and Cousine; Copyright (c) 2012 Red Hat, Inc. with Reserved Font Name Liberation | SIL Open Font License 1.1 |
| Carlito | google/fonts ofl/carlito | Word to PDF where Calibri is not installed | Copyright 2013 The Carlito Project Authors, with Reserved Font Name "Carlito" | SIL Open Font License 1.1 |
| Caladea | 1.002 (crosextrafonts-20130214.tar.gz, Chrome OS mirror; the later OFL release on Google Fonts no longer has Cambria's widths) | Word to PDF where Cambria is not installed | Copyright (c) 2012 Huerta Tipografia | Apache-2.0 |

Build and test tools that are not shipped (validators, test frameworks, signing
tools) are not listed.

## SIL Open Font License 1.1

The fonts DM Sans and Manrope ship in the Azure package's web console with their full
licence texts, `web/public/fonts/dm-sans-OFL.txt` and `web/public/fonts/manrope-OFL.txt`,
which an Azure instance also serves at `/fonts/dm-sans-OFL.txt` and `/fonts/manrope-OFL.txt`.
The Windows app carries static files cut from those two fonts (Regular, Medium, SemiBold and Bold of each; DM Sans
at the optical size 14) as resources inside `HiveBDC.App.dll`; `build/make-static-fonts.py` makes them with fontTools,
outlines and metrics unchanged. Neither licence declares a Reserved Font Name, so the cut files keep the names DM Sans
and Manrope. The licence texts are installed next to the app as `Assets\Fonts\dm-sans-OFL.txt` and
`Assets\Fonts\manrope-OFL.txt`.

The fonts Liberation Sans, Serif and Mono and Carlito ship unmodified inside the service
with their full licence texts, `Documents/Word/Fonts/liberation-OFL.txt` and
`carlito-OFL.txt`; Caladea (Apache-2.0, below) ships with `caladea-Apache-2.0.txt`.
SHA-256 of the shipped font files:

- `Caladea-Bold.ttf` 74eda4fc5ffba0d8dc4aa76d41499f5ab76168d9ed6141c6417064c6244db9b6
- `Caladea-BoldItalic.ttf` f47a35ad6cd0efa9914d93f9d03c93e30c55da43674bccfabac73b3c0d522c01
- `Caladea-Italic.ttf` 9c968bf60ba1e851cdfea77c71e3540c57792f77482dd241acc29d1425569c4e
- `Caladea-Regular.ttf` d2f6cad33f191e65b68bd74e6d4f7708080a41b32db635866109df3090265d91
- `Carlito-Bold.ttf` bb5d20f79b82599ec72983597437373a80f2d2085fa91fc144fd74e876a594db
- `Carlito-BoldItalic.ttf` b32928186c119599e03ca6a1ffc680fdcb7fac95772f4b95d989cf6cd3861517
- `Carlito-Italic.ttf` 0b019225e58d702bfedcbd35c21696769f8ee115cb6343f84c2f240312450d1c
- `Carlito-Regular.ttf` f6418f708baede9789daef5d458c0f53d2a888af9820e8062934e504fedc6595
- `LiberationMono-Bold.ttf` bd62a0672d0b9b6710b01df434c80ad54fa5f0835207eb7b17b7a761463067bb
- `LiberationMono-BoldItalic.ttf` 79451f3c09fe25116098853b7a2ca6e2436220ccc11af022979adbcf195be130
- `LiberationMono-Italic.ttf` 605c01c711b44480a7508d349dfbf3264e81fa43d69e61cfa7d10b86e764c4d1
- `LiberationMono-Regular.ttf` f2b83c763e8afd21709333370bed4774337fae82267937e2b5aea7e2fbd922c1
- `LiberationSans-Bold.ttf` 788abee4c806d660e8aee46689dd8540cd4bb98da03dcc9d171ce3efd99a9173
- `LiberationSans-BoldItalic.ttf` 698da70fc191cc5f33ad4d6d3fe830fe4624b898ea2e3169955928b7c491f1ee
- `LiberationSans-Italic.ttf` e5bae5c4cde31f22142753855f4f8fb86da6ff39955ed3c0a11248b0d16948b0
- `LiberationSans-Regular.ttf` 76d04c18ea243f426b7de1f3ad208e927008f961dc5945e5aad352d0dfde8ee8
- `LiberationSerif-Bold.ttf` d754ba427cfe0bca54ae052384baa8f842da5bd6550ad4da024ac441e7a7d5ce
- `LiberationSerif-BoldItalic.ttf` f17db8af71e24d2066b587546021d4f0b296be389512b658dec3c09affeb11a7
- `LiberationSerif-Italic.ttf` 0e3dea9f8d613e006ccfa62201f33e265d19167bd0907725c3e145368b04fc2e
- `LiberationSerif-Regular.ttf` 058ea80864aef09a23f45cbec2bb5400bc3dfbdea01c3f10538a21fcb497fb74

## ISC License

Applies to lucide-react, whose icon path data the Windows app also carries (`app/HiveBDC.App/Theme/Icons.xaml`).

Copyright (c) for portions of Lucide are held by Cole Bemis 2013-2026 as part of Feather (MIT). All other copyright (c) for Lucide are held by Lucide Contributors 2026.

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

The Feather portions are under the MIT License below.

## MIT License

Applies to the components marked MIT above, each with the copyright line given
in the table.

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

## Apache License 2.0

Applies to the components marked Apache-2.0 above, each with the copyright line
given in the table. None of them ships a NOTICE file.

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
      boilerplate notice, with the fields enclosed by brackets "{}"
      replaced with your own identifying information. (Don't include
      the brackets!)  The text should be enclosed in the appropriate
      comment syntax for the file format. We also recommend that a
      file or class name and description of purpose be included on the
      same "printed page" as the copyright notice for easier
      identification within third-party archives.

   Copyright {yyyy} {name of copyright owner}

   Licensed under the Apache License, Version 2.0 (the "License");
   you may not use this file except in compliance with the License.
   You may obtain a copy of the License at

       http://www.apache.org/licenses/LICENSE-2.0

   Unless required by applicable law or agreed to in writing, software
   distributed under the License is distributed on an "AS IS" BASIS,
   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
   See the License for the specific language governing permissions and
   limitations under the License.

## SQLite

The SQLite library is in the public domain. Its authors disclaim copyright:
"The author disclaims copyright to this source code. In place of a legal
notice, here is a blessing: May you do good and not evil. May you find
forgiveness for yourself and forgive others. May you share freely, never
taking more than you give."
