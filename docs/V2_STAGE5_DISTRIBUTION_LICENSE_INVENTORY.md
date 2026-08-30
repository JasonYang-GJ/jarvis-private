# 元枢 V2 Stage 5 分发许可离线清单

## 1. 状态与结论

- Packet：`s5-r1-offline-license-inventory`
- 审计基线：`02b910862d1daba68629b8631d49e149983f74f5`
- Stage 4 tag：`v0.6.0-stage4`
- Tag object：`20045c7960c182a052a5e0b2552ce0ed14a3863f`
- Tag target：`3a591a7b6af7da7d97e07093d4c33a3f44553b82`
- S5-R1 Offline Inventory：**PASS**
- S5-R1 overall：**BLOCKED_PENDING_OFFICIAL_LICENSE_EVIDENCE**
- External Distribution：**BLOCKED**
- Stage 5 产品/安装器实施：**NOT_STARTED / NOT_AUTHORIZED**

本清单是本机、离线、只读证据整理，不是法律意见，不建立 `PROHIBITED` 或 `INCOMPATIBLE` 结论。`UNKNOWN_BLOCKED_FOR_DISTRIBUTION` 表示当前证据不足，必须在分发前补齐官方材料并重新获得 exact-SHA Owner 授权；它不等于认定权利人禁止分发。

## 2. 证据边界与方法

- 50 行 runtime package 清单只从以下两个冻结 RID 锁文件机械去重得出：
  - `src/ScreenGuide.DesktopClient/packages.win-x64.lock.json`
  - `src/ScreenGuide.DesktopHost/packages.win-x64.lock.json`
- 唯一键为 package ID + exact resolved version；项目引用不计入 NuGet package 行。
- `contentHash` 原样来自锁文件，可用于绑定包内容，但本身不是许可证明。
- 本机缓存证据只使用规范化的 `nuget-cache/<package>/<version>/...` 引用；不记录用户绝对路径。
- nuspec expression 只作为元数据分类，不能代替与该 exact version 绑定的完整 LICENSE/NOTICE/third-party notices。
- 本轮没有网络、Provider、凭据、安装器、GUI、麦克风、测试或构建活动，也没有修改锁文件。

## 3. 精确 runtime package 清单

机械提取结果：50 个唯一 package+version。分组计数：Microsoft.Data.Sqlite=2、Microsoft.Extensions=27、NAudio=7、Sherpa=9、SQLitePCLRaw=4、System.Speech=1。除 NAudio 2.2.1 主包外，49 行均为 `UNKNOWN_BLOCKED_FOR_DISTRIBUTION`；其中 nuspec 元数据为 MIT 36 行、Apache-2.0 13 行。

| # | Package | Version | Group | contentHash | Local evidence category |
|---:|---|---|---|---|---|
| 1 | `Microsoft.Data.Sqlite` | `10.0.11` | Microsoft.Data.Sqlite | `7je7UELzm131GiLYc4PpZvfKXIgIyzPM+v+tjcd/nbnuWRfgcONYKzDTqJlURxwVCFsVnlpmq6y6yn4qvR8QXQ==` | `UNKNOWN_NUSPEC_METADATA_ONLY_MIT` |
| 2 | `Microsoft.Data.Sqlite.Core` | `10.0.11` | Microsoft.Data.Sqlite | `hubA20AGenQ4Sx0ElWaPpB8DISjXpdx463+1zOGRslsT0e/t/06ITv+pHsop8CcJ0d8PZLfgnT7juCDVD79Dkw==` | `UNKNOWN_NUSPEC_METADATA_ONLY_MIT` |
| 3 | `Microsoft.Extensions.Configuration` | `10.0.11` | Microsoft.Extensions | `wlhRqZW8LcJPa+vk2oLAc/REXDItHtkFQdf/QcXYGZbZOO13izcsKY1pCvuFQYwUiZD+hwSZwsKASjqT+BNaVg==` | `UNKNOWN_NUSPEC_METADATA_ONLY_MIT` |
| 4 | `Microsoft.Extensions.Configuration.Abstractions` | `10.0.11` | Microsoft.Extensions | `fVi053xdpda9Em7vSkmgVxO/PtgC2m78ekReKWsgcyskqY0U82Bz/MONwxpGzI0hElYKJfw+fupqMVeKW3fSaA==` | `UNKNOWN_NUSPEC_METADATA_ONLY_MIT` |
| 5 | `Microsoft.Extensions.Configuration.Binder` | `10.0.11` | Microsoft.Extensions | `rFn8RuszZn3qquPVkDytMUlPc2+rXl9MCoygwc1XmAgC5vg5/oXJ8hkOosOrLoBLsqdTy4lFwP6iQdPS9uSYOA==` | `UNKNOWN_NUSPEC_METADATA_ONLY_MIT` |
| 6 | `Microsoft.Extensions.Configuration.CommandLine` | `10.0.11` | Microsoft.Extensions | `1KHr/1L56llwQ/yI0tAisEA31UpPsn8aasjASIwELOaN4JIUcbjuQBMdFOIzfNBBeULoUa0XfBe5QDtRRUY+fg==` | `UNKNOWN_NUSPEC_METADATA_ONLY_MIT` |
| 7 | `Microsoft.Extensions.Configuration.EnvironmentVariables` | `10.0.11` | Microsoft.Extensions | `KICyU3eVi5jvloKm01EXV69L97H/zkhISVtV98cIuzuFOxNx3xTUVcXqvWTz3aq7OvUuDB/MFlPFjmxRaKF7/A==` | `UNKNOWN_NUSPEC_METADATA_ONLY_MIT` |
| 8 | `Microsoft.Extensions.Configuration.FileExtensions` | `10.0.11` | Microsoft.Extensions | `mDW7KVFB05M6jiRUyaZiOMWhS31n5HlSZwoYctHAZAucD4sMDJ70IxOmkGDt6RpstchD+keWBjhdzcMpSkWvWQ==` | `UNKNOWN_NUSPEC_METADATA_ONLY_MIT` |
| 9 | `Microsoft.Extensions.Configuration.Json` | `10.0.11` | Microsoft.Extensions | `nSPrT8U/cNoB4coqkmnanAMK9PsL7lsjG+LLUKEwHRFwS6E78b8S1wdv/y88EOxBhasWov1rLd7RTHmmsYPOLg==` | `UNKNOWN_NUSPEC_METADATA_ONLY_MIT` |
| 10 | `Microsoft.Extensions.Configuration.UserSecrets` | `10.0.11` | Microsoft.Extensions | `BRliLdUowglV8GS+J1G/QsSofCJYYFg3U8QZx0ACRn+a91az/Qnpy+h6PyHS94WgV2TSazX5D/cuuk6wnCJatw==` | `UNKNOWN_NUSPEC_METADATA_ONLY_MIT` |
| 11 | `Microsoft.Extensions.DependencyInjection` | `10.0.11` | Microsoft.Extensions | `PSmotV19c7E3lKed++uYo1kSiXFI+uTl37CBSrhq+CfLC3FCHjG7R91+xPnNehQfHS1b0Tzo/CCLPWH3qaEheg==` | `UNKNOWN_NUSPEC_METADATA_ONLY_MIT` |
| 12 | `Microsoft.Extensions.DependencyInjection.Abstractions` | `10.0.11` | Microsoft.Extensions | `/a1aJz4m7ylhEDf25ugQChLQoN5XwoGjWw/BoR/ZWWKsO1v4DdJElS1uyngahz4B/eOzjFk1KNTkarRLE5wsIg==` | `UNKNOWN_NUSPEC_METADATA_ONLY_MIT` |
| 13 | `Microsoft.Extensions.Diagnostics` | `10.0.11` | Microsoft.Extensions | `HT70uGPxMLqqnOzKMcnQtDmeV4r0KHr4qVCLhP7SXil9jMEm8sQXwcybxVVFGXZJ1V44xV0mLqQ54aZbcR2OiQ==` | `UNKNOWN_NUSPEC_METADATA_ONLY_MIT` |
| 14 | `Microsoft.Extensions.Diagnostics.Abstractions` | `10.0.11` | Microsoft.Extensions | `se7Kx8QpJEt+nf26L4qIVAofGTDr1wbexxsh/Fm3Xc04xUkqUXK06KUS7FLwSQYSjqb7q9n+T7MEcXYBhI1Y5g==` | `UNKNOWN_NUSPEC_METADATA_ONLY_MIT` |
| 15 | `Microsoft.Extensions.FileProviders.Abstractions` | `10.0.11` | Microsoft.Extensions | `JOjac6SQQgZmdmB8WGEw61/7siqMZoWJMkmq2p1goJGxqI59lO6oB4bOl0jNsbaPBdYy5Mlkb+6U7T4+CjnD8Q==` | `UNKNOWN_NUSPEC_METADATA_ONLY_MIT` |
| 16 | `Microsoft.Extensions.FileProviders.Physical` | `10.0.11` | Microsoft.Extensions | `Tq/UqMaczePv9yWwSsJZRgKtgA46djVR5xHj/lZBCueQ3ag8f9v5mu0EdhrNx7tXxNk+Y9OurG2oKuSKINjr0A==` | `UNKNOWN_NUSPEC_METADATA_ONLY_MIT` |
| 17 | `Microsoft.Extensions.FileSystemGlobbing` | `10.0.11` | Microsoft.Extensions | `2i6rtW/B5rCnWCnhdmWWEmaM9O0HD0zsPY9eRqa++y4tclI3Uw8zvGbBvhY/LjAdtf8gUHhUPcAWj3DRlWMXmQ==` | `UNKNOWN_NUSPEC_METADATA_ONLY_MIT` |
| 18 | `Microsoft.Extensions.Hosting` | `10.0.11` | Microsoft.Extensions | `eIDa/Rl+93aj17gMlFsJJx+LhBvb3CP0Mu1PeVYkDp2Y3S4Jock8UynfGQEcx7lrlq+gKW+ECQJHbro/LTPDEQ==` | `UNKNOWN_NUSPEC_METADATA_ONLY_MIT` |
| 19 | `Microsoft.Extensions.Hosting.Abstractions` | `10.0.11` | Microsoft.Extensions | `pwtpF7iF/NNaOBcX+pvMZ7y2+JAVbH5KkNrH9uMZtuVxVJsFTDiWiCR7Tk3HVptsAijaetipUZVRVK2LLq+nvA==` | `UNKNOWN_NUSPEC_METADATA_ONLY_MIT` |
| 20 | `Microsoft.Extensions.Logging` | `10.0.11` | Microsoft.Extensions | `nUOJwgFkSiLHiVGFpU22pIJtuWYewuSYQ3JVuP/gdK8ASMT807Px+TYQiRWs6uSsOmoyFTaVCwKXTasczV6BpA==` | `UNKNOWN_NUSPEC_METADATA_ONLY_MIT` |
| 21 | `Microsoft.Extensions.Logging.Abstractions` | `10.0.11` | Microsoft.Extensions | `Ljd0Uxoq5XpScD2Bg0nM/r3mwx7Ao5Uq24eo2ARxbGvqJ7Zht6rt2cJtwVRH4Cv+1ZVMdXz6TB43KbpmsxRrvQ==` | `UNKNOWN_NUSPEC_METADATA_ONLY_MIT` |
| 22 | `Microsoft.Extensions.Logging.Configuration` | `10.0.11` | Microsoft.Extensions | `S7LvLeVHKNPaY2NMyxW7c2TBGsLgxoSUBCV5Ev5iN8kgC7EPR2UB7eW7vHsElGMcIUDwRmoxLfvGDynCn3q6EA==` | `UNKNOWN_NUSPEC_METADATA_ONLY_MIT` |
| 23 | `Microsoft.Extensions.Logging.Console` | `10.0.11` | Microsoft.Extensions | `dFc0yDudyD1iIg6z9XT7ofsT3hVO7Y4ylrxGHIVRR0GaZ4CUk4ujOrMoy7wWEdNHZhvJooySg6hZpOxyS8zEVA==` | `UNKNOWN_NUSPEC_METADATA_ONLY_MIT` |
| 24 | `Microsoft.Extensions.Logging.Debug` | `10.0.11` | Microsoft.Extensions | `wr+j1bjdFXhc8lKTLoq+RbwFM8M+orcMS9xrcqLmDGOxJcXpKizEeE5h6v/GKwCZV02FmhaA7OlNjoq072jZpQ==` | `UNKNOWN_NUSPEC_METADATA_ONLY_MIT` |
| 25 | `Microsoft.Extensions.Logging.EventLog` | `10.0.11` | Microsoft.Extensions | `Eck9GpCCpvZ3f6L7IUlN+mPtRVefnf7PsiIG5vi61QawPtLNCEAv2TPD/M3SojcU0PFaef+BxiVGPOShFHtDog==` | `UNKNOWN_NUSPEC_METADATA_ONLY_MIT` |
| 26 | `Microsoft.Extensions.Logging.EventSource` | `10.0.11` | Microsoft.Extensions | `hs6QWECLLohi2VKqUvSGRUvrg7eXR1DqKL95Jrtz3cdD2g2nBA+yJPdRQLZ7SLmnTZWycxfMDK2s0ho+rfst5w==` | `UNKNOWN_NUSPEC_METADATA_ONLY_MIT` |
| 27 | `Microsoft.Extensions.Options` | `10.0.11` | Microsoft.Extensions | `eY1GAKcTfD2maP27J84X9IovT3yjHJ2dVDzPmDg6/XqYvt3jMzJhtfQCLjG9pVsZGAd+8DQ2QrjaDcs2+VQLGw==` | `UNKNOWN_NUSPEC_METADATA_ONLY_MIT` |
| 28 | `Microsoft.Extensions.Options.ConfigurationExtensions` | `10.0.11` | Microsoft.Extensions | `syEhXQ/sEaSBFaqzlp9gDGHX/nk6gkQkh1sIUpBO1mlBj3Phu1rmb4ML1uCiyPW9N6Kxfxv3y5FGObC+bV01Qw==` | `UNKNOWN_NUSPEC_METADATA_ONLY_MIT` |
| 29 | `Microsoft.Extensions.Primitives` | `10.0.11` | Microsoft.Extensions | `SXcz+kF+4Oo9b1+55zntpJFYfwb1jw66ioxptyNOOTDc8g2FHnBFWjZpsWfCvZIhzr0x+4e2trVTs4OKwQfBtw==` | `UNKNOWN_NUSPEC_METADATA_ONLY_MIT` |
| 30 | `NAudio` | `2.2.1` | NAudio | `c0DzwiyyklM0TP39Y7RObwO3QkWecgM6H60ikiEnsV/aEAJPbj5MFCLaD8BSfKuZe0HGuh9GRGWWlJmSxDc9MA==` | `CONFIRMED_BY_LOCAL_LICENSE` |
| 31 | `NAudio.Asio` | `2.2.1` | NAudio | `hQglyOT5iT3XuGpBP8ZG0+aoqwRfidHjTNehpoWwX0g6KJEgtH2VaqM2nuJ2mheKZa/IBqB4YQTZVvrIapzfOA==` | `UNKNOWN_NUSPEC_METADATA_ONLY_MIT` |
| 32 | `NAudio.Core` | `2.2.1` | NAudio | `GgkdP6K/7FqXFo7uHvoqGZTJvW4z8g2IffhOO4JHaLzKCdDOUEzVKtveoZkCuUX8eV2HAINqi7VFqlFndrnz/g==` | `UNKNOWN_NUSPEC_METADATA_ONLY_MIT` |
| 33 | `NAudio.Midi` | `2.2.1` | NAudio | `6r23ylGo5aeP02WFXsPquz0T0hFJWyh+7t++tz19tc3Kr38NHm+Z9j+FiAv+xkH8tZqXJqus9Q8p6u7bidIgbw==` | `UNKNOWN_NUSPEC_METADATA_ONLY_MIT` |
| 34 | `NAudio.Wasapi` | `2.2.1` | NAudio | `lFfXoqacZZe0WqNChJgGYI+XV/n/61LzPHT3C1CJp4khoxeo2sziyX5wzNYWeCMNbsWxFvT3b3iXeY1UYjBhZw==` | `UNKNOWN_NUSPEC_METADATA_ONLY_MIT` |
| 35 | `NAudio.WinForms` | `2.2.1` | NAudio | `DlDkewY1myY0A+3NrYRJD+MZhZV0yy1mNF6dckB27IQ9XCs/My5Ip8BZcoSHOsaPSe2GAjvoaDnk6N9w8xTv7w==` | `UNKNOWN_NUSPEC_METADATA_ONLY_MIT` |
| 36 | `NAudio.WinMM` | `2.2.1` | NAudio | `xFHRFwH4x6aq3IxRbewvO33ugJRvZFEOfO62i7uQJRUNW2cnu6BeBTHUS0JD5KBucZbHZaYqxQG8dwZ47ezQuQ==` | `UNKNOWN_NUSPEC_METADATA_ONLY_MIT` |
| 37 | `org.k2fsa.sherpa.onnx` | `1.13.4` | Sherpa | `IT+CJrER3K8VWYXZNuJR2ufaiWvzlAiD2njSaLtHCeizFk8UolLZKsuYcY+VWi3f/CO5AXNrNGu7kMmpz+EkGQ==` | `UNKNOWN_NUSPEC_METADATA_ONLY_APACHE-2.0` |
| 38 | `org.k2fsa.sherpa.onnx.runtime.android-arm64` | `1.13.4` | Sherpa | `Z1CxJ86yz0ZTihW0wdNmMVMtHUeddzH/dzEATTOpqpePM9Mam2aQEuIkwIt23KxgLzrqqSDHnThLp7vR1ve+zQ==` | `UNKNOWN_NUSPEC_METADATA_ONLY_APACHE-2.0` |
| 39 | `org.k2fsa.sherpa.onnx.runtime.linux-arm64` | `1.13.4` | Sherpa | `ByuOqzOZumY5kMA+zF2HWLCr9KudwWi6Z1lfkgjx6W+RrglRwqBGm2esc6817VZ2Uk1eBZ4N7LP8rryZ31t8Vg==` | `UNKNOWN_NUSPEC_METADATA_ONLY_APACHE-2.0` |
| 40 | `org.k2fsa.sherpa.onnx.runtime.linux-x64` | `1.13.4` | Sherpa | `ow80s8w4jDkq9RpUwhS0S4TEXVgk20ywYtaKEAMbMLFFSmThjwel1hbCXxrzc16zjkMHsVuxYVf/BMGdAB6Lig==` | `UNKNOWN_NUSPEC_METADATA_ONLY_APACHE-2.0` |
| 41 | `org.k2fsa.sherpa.onnx.runtime.osx-arm64` | `1.13.4` | Sherpa | `YxDNdmQQj0Wj9IVgVG8iiPaEkJcGuVyk6F2l9+V7Kpj88ElsfqCIwno50CLpiVBAoQJzrpnnn+DlSlOJgffK8g==` | `UNKNOWN_NUSPEC_METADATA_ONLY_APACHE-2.0` |
| 42 | `org.k2fsa.sherpa.onnx.runtime.osx-x64` | `1.13.4` | Sherpa | `TXY2HI7zJigU80pxb1ZBByHHqBzOWEouP+NScYnmBP2WIUWQ0fAmizmMYOyko8izqRwozVRrdA5O7ZgAsvLF8w==` | `UNKNOWN_NUSPEC_METADATA_ONLY_APACHE-2.0` |
| 43 | `org.k2fsa.sherpa.onnx.runtime.win-arm64` | `1.13.4` | Sherpa | `NE/VCE1I6E4IvifCDv5oa9p5M7JatLAMSg7qdD2yz6W0GeykSF65UKn5o32o4FrUOteXjvJpMHIqGzLOa6SRQA==` | `UNKNOWN_NUSPEC_METADATA_ONLY_APACHE-2.0` |
| 44 | `org.k2fsa.sherpa.onnx.runtime.win-x64` | `1.13.4` | Sherpa | `HETGez6u+K+h7YF8nE40iXWrEjOIKDASGqlqP8dOJf5THMpEUmRlxgr9Mvlu7bg0TeAUI3gdBDY3Ejhwdl1HNw==` | `UNKNOWN_NUSPEC_METADATA_ONLY_APACHE-2.0` |
| 45 | `org.k2fsa.sherpa.onnx.runtime.win-x86` | `1.13.4` | Sherpa | `/sl2z1Tvhvated2kS9KWV4GBEv0tgW6F/2t+3HAYJzO0cWNs7onCQHpuc/egxsFQHYo7G8DVWyQWtsfICkDFaQ==` | `UNKNOWN_NUSPEC_METADATA_ONLY_APACHE-2.0` |
| 46 | `SQLitePCLRaw.bundle_e_sqlite3` | `2.1.12` | SQLitePCLRaw | `mAgscpQMLw5/nfA1Q5oJVAT29yROUo1ifZGbbTpx/lwZpSxMUGoYbKfmvdm8oXER+RzxqBmmQzeBEVKfeHv2nw==` | `UNKNOWN_NUSPEC_METADATA_ONLY_APACHE-2.0` |
| 47 | `SQLitePCLRaw.core` | `2.1.12` | SQLitePCLRaw | `ETpNw9DY3ckWLgRRAeCHj+GKOuPi61aeczkXhgHexUvqoZBAYg8RYESE2J7O1M7+o6QbdSEZwrw9bfqztUVWXg==` | `UNKNOWN_NUSPEC_METADATA_ONLY_APACHE-2.0` |
| 48 | `SQLitePCLRaw.lib.e_sqlite3` | `2.1.12` | SQLitePCLRaw | `fWi8Dbknuhgg72fWinIdjXVaqO1hHL4YBBwVLnr7e1c9TAZwJ0QE38j9syW1hwx6HaqEVTwI+O07WPdZn8Rp0w==` | `UNKNOWN_NUSPEC_METADATA_ONLY_APACHE-2.0` |
| 49 | `SQLitePCLRaw.provider.e_sqlite3` | `2.1.12` | SQLitePCLRaw | `W3oH4XIfCzFrgUSDKHhN6N+dgzA5YHOR2VxX8GB6Qy7CyrJJgxPEG8NirgYWlPQC5P2jz2knSsexWu4tDUL33g==` | `UNKNOWN_NUSPEC_METADATA_ONLY_APACHE-2.0` |
| 50 | `System.Speech` | `10.0.10` | System.Speech | `a6xJl9sq7UHVx2hfrrg1h1800d0dI1o8ETEQ5YxOiND/DG57B0cC+SRQKmVE/bKA7U0YwWXKdOfz5wG2x+UDCA==` | `UNKNOWN_NUSPEC_METADATA_ONLY_MIT` |

## 4. 已绑定的本机许可文件证据

### NAudio 2.2.1 主包

- 状态：`CONFIRMED_BY_LOCAL_LICENSE`，仅适用于表中 `NAudio` 2.2.1 主包这一行。
- 规范化引用：`nuget-cache/naudio/2.2.1/license.txt`
- Size：1,059 bytes
- SHA-256：`303E01786B271EB464B3F43D18A375F2F17D37BC708A37C2FDF45B289FB5BFAA`

其余六个 NAudio 子包仍为 `UNKNOWN_BLOCKED_FOR_DISTRIBUTION`；不能用主包许可文件自动覆盖不同 package ID。

### .NET self-contained 与 Windows SDK

这些文件构成部分本地证据，但尚未与 V0.6.0 标签产物建立完整逐文件 attribution，因此总状态仍为 `UNKNOWN_BLOCKED_FOR_DISTRIBUTION`：

| Component | Normalized evidence | Size | SHA-256 | Result |
|---|---|---:|---|---|
| Microsoft.NETCore.App.Runtime.win-x64 10.0.11 | `nuget-cache/microsoft.netcore.app.runtime.win-x64/10.0.11/LICENSE.TXT` | 1,139 | `D7A68596AB69B06F51CA278A6545148E4269A9381C26D597C13DF5D88E08CF5B` | partial local evidence |
| Microsoft.NETCore.App.Runtime.win-x64 10.0.11 | `nuget-cache/microsoft.netcore.app.runtime.win-x64/10.0.11/THIRD-PARTY-NOTICES.TXT` | 78,041 | `6D15E10A101C6BFFF2AB4429ED061BF76C456FC4B23AD6B03E0D0F8377148A21` | partial local evidence |
| Microsoft.WindowsDesktop.App.Runtime.win-x64 10.0.11 | `nuget-cache/microsoft.windowsdesktop.app.runtime.win-x64/10.0.11/LICENSE` | 1,137 | `A89886665765362EB77E0F8E26602C924520041D1711B2EEDC136434FE4D01AB` | partial local evidence |
| Microsoft.Windows.SDK.NET.Ref 10.0.19041.57 | `nuget-cache/microsoft.windows.sdk.net.ref/10.0.19041.57/microsoft.windows.sdk.net.ref.nuspec` | 1,345 | `AB5229D56BC17BE4F78440C326AA8376F0577C5121A7C0B5A70B7D2819C6CD9A` | metadata only; license URL `https://aka.ms/WinSDKLicenseURL` |

## 5. 项目自有内容与资产

- 项目源码、文档、runtime prompts、品牌和图标：`UNKNOWN_BLOCKED_FOR_DISTRIBUTION`。
- 仓库根没有 LICENSE、NOTICE、THIRD_PARTY 或 Owner ownership/source declaration。
- `.ico` 会嵌入 DesktopClient，runtime prompts 会复制到 DesktopHost publish；二者都必须有可追溯的所有权/来源声明。
- 没有发现随发布项目捆绑的字体、声音或录音；`yuanshu-icon.png` 不被 publish 项目引用，`.ico` 被捆绑。
- 不记录 Prompt 正文、模型内容或原始二进制内容。

## 6. Sherpa / ONNX 与语音模型

- 锁定包：`org.k2fsa.sherpa.onnx` 与 `org.k2fsa.sherpa.onnx.runtime.win-x64` 1.13.4。精确 win-x64 RID lock/package asset graph 声明或预期提供 `sherpa-onnx.dll`、`sherpa-onnx-c-api.dll`、`onnxruntime.dll`；这是依赖/包证据，不证明这些文件实际存在于冻结的 V0.6.0 tag artifact。
- 50 行锁表中的七个非 win-x64 Sherpa runtime package 只是 lock-graph entries；对 Windows x64 分发均为 `NOT_BUNDLED_RUNTIME_DEPENDENCY`。它们的 package license evidence 仍为 `UNKNOWN_BLOCKED_FOR_DISTRIBUTION`，但不得据此暗示其已随 Windows x64 安装包分发。
- 本地包没有与 exact 1.13.4 绑定的完整 LICENSE/NOTICE：`UNKNOWN_BLOCKED_FOR_DISTRIBUTION`。
- 当前安装包不捆绑语音模型或 tokens。运行时只从 `%LOCALAPPDATA%\ScreenGuideTeacher\models\sherpa-onnx-streaming-zipformer-zh-int8-2025-06-30` 读取四个文件；仓库没有该模型、tokens 或许可。
- 未来模型分发状态：`UNKNOWN_BLOCKED_FOR_DISTRIBUTION`，需要 exact model card、license、source、version 与 hash。

## 7. SQLite

- `Microsoft.Data.Sqlite` / `.Core` 10.0.11 与 `SQLitePCLRaw*` 2.1.12 见精确表。
- `e_sqlite3.dll` 的来源包可以定位到 `SQLitePCLRaw.lib.e_sqlite3`，但本地没有与冻结产物绑定的完整许可文本：`UNKNOWN_BLOCKED_FOR_DISTRIBUTION`。

## 8. DirectML、Inno、Provider 与 Codex

- DirectML：`NOT_BUNDLED_RUNTIME_DEPENDENCY`；允许的 manifests/locks 中没有该 runtime dependency。
- Inno Setup compiler：`BUILD_TOOL_NOT_DISTRIBUTED`。生成的 installer engine 与 `ChineseSimplified.isl` 的许可/翻译条款尚未建立本地绑定证据，因此为 `UNKNOWN_BLOCKED_FOR_DISTRIBUTION`。
- DeepSeek/Qwen 等 Provider 模型/API 与 Codex CLI：`NOT_BUNDLED_RUNTIME_DEPENDENCY`。云服务/API 使用权不能当作 installer redistribution 权利，也不应混入本地 package 许可清单。

## 9. 冻结产物证据边界

- V0.6.0 基线文档是当前唯一权威的冻结 installer evidence，只证明 installer size/hash/`NotSigned`、ProductVersion/FileVersion 与 539 个发布文件，不证明每个文件的许可 attribution。
- 当前仓库根的 ignored `artifacts` 是非权威、可变的构建输出，不是只读 `v0.6.0-stage4` tag artifact manifest。当前 `artifacts/publish` 有 539 个文件，Client/Host informational version 绑定 `693719d09cead42304d7c3334b98e9161128c623`，不是正式 C0 `3a591a7b6af7da7d97e07093d4c33a3f44553b82`，因此不能用于 attribution。
- 当前 `artifacts/release` 含一个 V0.6.0 命名的 installer，QA 观察大小为 64,216,176 bytes；它不是正式冻结的 tag-source installer identity，同样不得用于 attribution。
- 未来只能对只读 V0.6.0 tag artifact 生成逐文件 manifest；不得移动 tag、重建 C0 identity 或把其他版本目录冒充冻结产物。

## 10. 相互独立的发布门禁

以下门禁彼此独立，不能互相替代：

1. License/NOTICE/attribution 完整性；
2. 数字签名与私钥安全；
3. 隔离干净 Windows 的 same-AppId install/upgrade/rollback/uninstall 生命周期；
4. tag、version、hash、signature 与 per-file release identity。

任一项缺失都保持 External Distribution=`BLOCKED`。

## 11. 最小未来证据

未来在线核验必须使用新的 exact-SHA Owner 授权；本轮不浏览。需要的官方域名/材料类别至少包括：

- `nuget.org`：每个 exact package/version 的官方包页、owner 与原始 package metadata，且仍须获取完整 LICENSE/NOTICE；
- `github.com/naudio`：NAudio exact version 的完整许可与 notices；
- `learn.microsoft.com`、`dotnet.microsoft.com`：.NET runtime、WindowsDesktop、Windows SDK 的 redistribution、LICENSE 与 third-party notices；
- `github.com/k2-fsa`、`k2-fsa.github.io`：Sherpa 1.13.4 的 LICENSE/NOTICE、依赖 attribution，以及 exact voice model 的 model card/license/source/version/hash；
- `jrsoftware.org`：Inno Setup compiler/engine redistribution 条款；另需 `ChineseSimplified.isl` 的翻译者/来源许可材料；
- Owner 本地声明：项目源码、文档、Prompt、品牌、`.ico` 的 ownership/source/distribution declaration；
- 只读 `v0.6.0-stage4` artifact：完整 per-file manifest、来源映射、LICENSE/NOTICE placement 与 attribution 检查。

在上述证据补齐并通过独立 Gate 前，不得把 `BLOCKED_PENDING_OFFICIAL_LICENSE_EVIDENCE` 改为可分发。

## 12. 安全与治理

- 本文不包含用户绝对路径、秘密、Prompt 正文、模型内容、原始二进制内容或法律结论。
- 本文是唯一详细 S5-R1 offline inventory；`ROADMAP.md`、`PRODUCT.md`、`MEMORY.md` 与 Charter 只保留摘要和链接。
- 不建立第二套 license registry、数据库或 schema；Module Registry 保持 Shadow，不写 Registry/Lease。
- 本轮计数：NetworkRequests=0、ProviderRequests=0、CredentialReads=0、InstallerRuns=0、GUI=0、Microphone=0、Tests=0、Builds=0。
