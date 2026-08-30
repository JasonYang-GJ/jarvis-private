# 元枢 V2 Stage 5 分发许可离线清单

## 1. 状态与结论

- Packet：`s5-r1-frozen-attribution`
- Evidence lineage：`s5-r1-offline-license-inventory` → `s5-r1-official-license-evidence` → `s5-r1-frozen-attribution`
- 审计基线：`02b910862d1daba68629b8631d49e149983f74f5`
- Stage 4 tag：`v0.6.0-stage4`
- Tag object：`20045c7960c182a052a5e0b2552ce0ed14a3863f`
- Tag target：`3a591a7b6af7da7d97e07093d4c33a3f44553b82`
- S5-R1 Offline Inventory：**PASS**
- S5-R1 Official Evidence Verification：**PASS**
- S5-R1 Attribution / NOTICE Contract：**S5-R1_CONTRACT_PASS**
- S5-R1 Packaging / NOTICE Gate Infrastructure：**FAIL_CLOSED_INFRASTRUCTURE_IMPLEMENTED**
- S5-R1 NOTICE Implementation：**NOTICE_IMPLEMENTATION_PENDING**
- External Distribution：**EXTERNAL_DISTRIBUTION_BLOCKED**
- Stage 5 产品与可分发安装包实施：**NOT_STARTED / NOT_AUTHORIZED**；本轮只实现分发构建前置门禁，不产生可分发安装包。

本清单不是法律意见，不建立 `PROHIBITED` 或 `INCOMPATIBLE` 结论。`S5-R1_CONTRACT_PASS` 只表示四轴归属、manifest schema、NOTICE layout 与授权门禁已经形成合同；`FAIL_CLOSED_INFRASTRUCTURE_IMPLEMENTED` 只表示 release 脚本和 Inno 已接入确定性离线验证器。当前受版本控制的 bundle skeleton 仍含 OPEN/PARTIAL、没有可冒充正文的 LICENSE/NOTICE 文件和完整 payload 映射，因此稳定返回 `distribution_notice_bundle_incomplete`，在 restore、publish 和 installer compile 前停止。它不表示 NOTICE 已闭合或产品可对外分发。`UNKNOWN_BLOCKED_FOR_DISTRIBUTION` 表示当前证据不足，必须在分发前补齐材料并重新获得相应 Owner 授权；它不等于认定权利人禁止分发。

## 2. 证据边界与方法

- 50 行 runtime package 清单只从以下两个冻结 RID 锁文件机械去重得出：
  - `src/ScreenGuide.DesktopClient/packages.win-x64.lock.json`
  - `src/ScreenGuide.DesktopHost/packages.win-x64.lock.json`
- 唯一键为 package ID + exact resolved version；项目引用不计入 NuGet package 行。
- `contentHash` 原样来自锁文件，可用于绑定包内容，但本身不是许可证明。
- 本机缓存证据只使用规范化的 `nuget-cache/<package>/<version>/...` 引用；不记录用户绝对路径。
- nuspec expression 只作为元数据分类，不能代替与该 exact version 绑定的完整 LICENSE/NOTICE/third-party notices。
- 官方来源证据是新增独立层，访问日期为 2026-08-30；本提交只离线记录已关闭网络阶段的短摘要与 URL，不重新访问来源，也不覆盖本机锁文件/哈希事实。
- 本轮文档提交没有网络、Provider、凭据、安装器、GUI、麦克风、测试或构建活动，也没有修改锁文件。

## 3. 精确 runtime package 清单

机械提取结果：50 个唯一 package+version。分组计数：Microsoft.Data.Sqlite=2、Microsoft.Extensions=27、NAudio=7、Sherpa=9、SQLitePCLRaw=4、System.Speech=1。在原始本机证据层，除 NAudio 2.2.1 主包外，49 行均为 `UNKNOWN_BLOCKED_FOR_DISTRIBUTION`；其中 nuspec 元数据为 MIT 36 行、Apache-2.0 13 行。后续官方来源分类见第 11 节，不改写本表。

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

其余六个 NAudio 子包在本机缓存证据层仍为 `UNKNOWN_BLOCKED_FOR_DISTRIBUTION`；不能用主包许可文件自动覆盖不同 package ID。第 11 节记录的 exact-tag 官方证据是另一层判断。

### .NET self-contained 与 Windows SDK

这些文件构成部分本地许可/NOTICE 证据；新的 C0 payload attribution 已把实际冻结文件映射到对应 runtime/package，但适用许可选择、NOTICE placement 与分发条件尚未全部闭合，因此总状态仍为 `UNKNOWN_BLOCKED_FOR_DISTRIBUTION`：

| Component | Normalized evidence | Size | SHA-256 | Result |
|---|---|---:|---|---|
| Microsoft.NETCore.App.Runtime.win-x64 10.0.11 | `nuget-cache/microsoft.netcore.app.runtime.win-x64/10.0.11/LICENSE.TXT` | 1,139 | `D7A68596AB69B06F51CA278A6545148E4269A9381C26D597C13DF5D88E08CF5B` | partial local evidence |
| Microsoft.NETCore.App.Runtime.win-x64 10.0.11 | `nuget-cache/microsoft.netcore.app.runtime.win-x64/10.0.11/THIRD-PARTY-NOTICES.TXT` | 78,041 | `6D15E10A101C6BFFF2AB4429ED061BF76C456FC4B23AD6B03E0D0F8377148A21` | partial local evidence |
| Microsoft.WindowsDesktop.App.Runtime.win-x64 10.0.11 | `nuget-cache/microsoft.windowsdesktop.app.runtime.win-x64/10.0.11/LICENSE` | 1,137 | `A89886665765362EB77E0F8E26602C924520041D1711B2EEDC136434FE4D01AB` | partial local evidence |
| Microsoft.Windows.SDK.NET.Ref 10.0.19041.57 | `nuget-cache/microsoft.windows.sdk.net.ref/10.0.19041.57/microsoft.windows.sdk.net.ref.nuspec` | 1,345 | `AB5229D56BC17BE4F78440C326AA8376F0577C5121A7C0B5A70B7D2819C6CD9A` | metadata only; license URL `https://aka.ms/WinSDKLicenseURL` |

## 5. 项目自有内容与资产

- Owner 事实声明已记录为 `OWNER_DECLARATION_RECORDED`；它是 Owner 自述，不是仓库独立验证、法律结论或外部分发放行。
- Owner 声明：产品想法来自在抖音看到相似语音功能概念后的启发；没有从该创作者、GitHub 或其他网站下载或复制源码。本文只记录该自述，不把“概念启发”表述为已核验的实现来源。
- Owner 声明：没有其他个人或公司贡献代码、设计或文件；项目材料由 Owner 在 Codex/AI 协助下制作。本文不据此判断 AI 辅助输出的法律权属。
- Owner 声明：“元枢”品牌名称由 Owner 创建；当前图标由 Codex 按 Owner 指示生成。品牌和图标暂按 Owner 保留处理，但这只是保守边界，不是已选择的根许可证、独立 provenance 证明或外部分发许可。
- 本地图标身份：`assets/branding/yuanshu-icon.ico` SHA-256=`AA29199DC0F383CFF10DECFC6A88F917A2B83A5677AC7AABCBE851638007AD7E`；`assets/branding/yuanshu-icon.png` SHA-256=`4D0AB22213D9F3EC2CD1BE5171FEAEB0F625A616167CC4D9FC7353243AC5AE48`；两者首次加入 Git 的提交为 `23b0760cac636ffbdd81dfa71a9b97ba593b33c1`（2026-08-19T02:23:57+08:00）。`.ico` 嵌入 DesktopClient，`.png` 未被 publish 项目引用。
- 图标证据拆分如下；找到公开 OpenAI 条款变体不等于已经绑定 Owner/账户适用协议，也不等于商业使用、权属、唯一性、不侵权、商标或外部分发已获确认：

| State | Status | 边界 |
|---|---|---|
| `OPENAI_PUBLIC_OUTPUT_TERMS_FOUND` | `YES` | 两次获授权检索均观察到含 output allocation 条款的公开 OpenAI Terms 变体；只证明公开文本存在，不证明哪一变体适用于 Owner/账户。 |
| `APPLICABLE_OPENAI_ACCOUNT_TERMS_NOT_BOUND` | `OPEN` | 未读取或推断 Owner/账户所在地、账户协议或适用辖区，不能选择任一公开变体作为 governing agreement。 |
| `OPENAI_OUTPUT_TERMS_CLEARANCE_NOT_ESTABLISHED` | `OPEN` | 因适用账户条款未绑定，不能关闭 OpenAI 输出条款 clearance，也不能从公开变体推出 artifact provenance 或分发许可。 |
| `OWNER_PROVENANCE_DECLARED` | `RECORDED_NOT_INDEPENDENTLY_PROVEN` | Owner 声明图标由 Codex 按其指示生成；仓库只能绑定本地文件身份和首次提交，不能独立证明生成会话。 |
| `AI_DISCLOSURE_REQUIRED` | `OPEN` | 对外使用时必须保留适当 AI 生成披露，不能把 AI 输出误称为纯人工生成。 |
| `COPYRIGHTABILITY_AND_UNIQUENESS_NOT_DETERMINED` | — | 条款说明输出可能不唯一；本文不判断图标是否可受版权保护或具有唯一性。 |
| `THIRD_PARTY_RIGHTS_REVIEW_REQUIRED` | `OPEN` | 仍须检查第三方权利、人物肖像及其他适用权利；不能从 OpenAI 条款推定不存在第三方权利。 |
| `TRADEMARK_CLEARANCE_NOT_PERFORMED` | `OPEN` | 未执行“元枢”名称或图标的商标检索/clearance；条款不保证贸易或商业使用中的商标保护。 |
| `ICON_EXTERNAL_DISTRIBUTION_CLEARANCE_NOT_ESTABLISHED` | `OPEN` | 不得据本节宣称图标可商业分发、完全归 Owner、可版权保护、唯一、不侵权或已获商标放行。 |

- Owner 声明未来可能付费销售编译产品和/或提供付费服务；状态仅为 `COMPILED_COMMERCIAL_INTENT_DECLARED`，不构成分发许可。源码公开分发当前为 `SOURCE_DISTRIBUTION_NOT_AUTHORIZED` / undecided。
- 仓库根仍没有 LICENSE、NOTICE 或 THIRD_PARTY；Owner 声明不替代第三方 package 许可证，也不解除现有归属、NOTICE、签名和安装生命周期门禁。
- runtime prompts 会复制到 DesktopHost publish；图标与 runtime Prompt 等自有材料仍需随 frozen manifest 和 NOTICE 实施闭合归属。
- 没有发现随发布项目捆绑的字体、声音或录音；`yuanshu-icon.png` 不被 publish 项目引用，`.ico` 被捆绑。
- 不记录 Prompt 正文、模型内容或原始二进制内容。

## 6. Sherpa / ONNX 与语音模型

- 锁定包：`org.k2fsa.sherpa.onnx` 与 `org.k2fsa.sherpa.onnx.runtime.win-x64` 1.13.4。精确 win-x64 RID lock/package asset graph 声明或预期提供 `sherpa-onnx.dll`、`sherpa-onnx-c-api.dll`、`onnxruntime.dll`；这是依赖/包证据，不证明这些文件实际存在于冻结的 V0.6.0 tag artifact。
- 50 行锁表中的七个非 win-x64 Sherpa runtime package 只是 lock-graph entries；对 Windows x64 分发均为 `NOT_BUNDLED_RUNTIME_DEPENDENCY`。它们的 package license evidence 仍为 `UNKNOWN_BLOCKED_FOR_DISTRIBUTION`，但不得据此暗示其已随 Windows x64 安装包分发。
- Sherpa ONNX 1.13.4 exact release/commit 的 Apache-2.0 LICENSE，以及其绑定的 ONNX Runtime 1.27.0 MIT LICENSE/ThirdPartyNotices 已由官方 GET-only 证据核实；C0 frozen files 也已完成逐文件归属。但安装包内 NOTICE placement 尚未实施，当前 cache package container 也不能冒充历史 C0 container，因此 External Distribution 仍失败关闭。
- 当前安装包不捆绑语音模型或 tokens。运行时只从 `%LOCALAPPDATA%\ScreenGuideTeacher\models\sherpa-onnx-streaming-zipformer-zh-int8-2025-06-30` 读取四个文件；仓库没有该模型、tokens 或许可。
- 未来模型分发状态：`UNKNOWN_BLOCKED_FOR_DISTRIBUTION`，需要 exact model card、license、source、version 与 hash。

## 7. SQLite

- `Microsoft.Data.Sqlite` / `.Core` 10.0.11 与 `SQLitePCLRaw*` 2.1.12 见精确表。
- `e_sqlite3.dll` 已逐文件绑定到 `SQLitePCLRaw.lib.e_sqlite3` 2.1.12；exact release/tag LICENSE/NOTICE、exact native package metadata 与 sqlite.org public-domain 说明均已核。Source 官方许可证据已闭合，但适用材料尚未布置进 frozen installer，当前 cache container 也不是历史 C0 package container，因此 NOTICE 轴仍为 `UNKNOWN`、External Distribution 仍失败关闭。

## 8. DirectML、Inno、Provider 与 Codex

- DirectML：`NOT_BUNDLED_RUNTIME_DEPENDENCY`；允许的 manifests/locks 中没有该 runtime dependency。
- Inno Setup compiler：`BUILD_TOOL_NOT_DISTRIBUTED`。JR Software 官方许可允许包括商业用途在内的使用与再分发；但实际 compiler exact 小版本和本地 `ChineseSimplified.isl` 的 exact upstream provenance 仍未绑定，因此只升级为 `PARTIAL`，不形成 installer clearance。
- DeepSeek/Qwen 等 Provider 模型/API 与 Codex CLI：`NOT_BUNDLED_RUNTIME_DEPENDENCY`。云服务/API 使用权不能当作 installer redistribution 权利，也不应混入本地 package 许可清单。

## 9. 冻结产物证据边界

- V0.6.0 基线文档证明冻结 installer 的 size/hash/`NotSigned`、ProductVersion/FileVersion 与 539 个发布文件；本轮经 Owner 明确授权，对保留的原始 tag-source C0 只读生成 `docs/baselines/V0.6.0_STAGE4_C0_STATIC_MANIFEST.json`，以 1 条 `installerContainer` 与 539 条 `installedPayload` 记录补充相对路径、size 和 SHA-256 静态身份。它仍不自动证明每个文件的许可 attribution。
- C0 installer 的**产品 payload**收纳合同只包含 win-x64 publish 树与删除脚本；Inno engine/translation 属于 installer 基础设施而不是额外产品 payload。本轮没有执行或解包 installer，因此 manifest 的 `installedPayload` 仅覆盖保留的 539 文件 publish 树，`installerContainer` 仅绑定 installer 本体，未枚举 Inno engine、translation 或其他 container entry，也不声称得到安装后文件系统。
- 中文语音模型在当前 installer 中为 `VERIFIED-EXCLUDED`，不再作为当前安装包的硬阻断；未来若改为捆绑或下载，必须重新进入独立许可 Gate。
- 静态 manifest 在保留的 publish 树中确认存在 `Microsoft.Windows.SDK.NET.dll`；其 SHA-256 与本地锁定包 `Microsoft.Windows.SDK.NET.Ref` 10.0.19041.57 中同名 DLL 完全一致，文件到 package 的精确绑定已闭合。Microsoft 官方 exact package page 与 REDIST 清单明确列出该 package 及 `./lib/net8.0/Microsoft.Windows.SDK.NET.dll`，并在适用许可条款条件下支持以未修改 package 或作为启用 WinRT API 调用的程序组成部分分发。Exact C0 源码直接使用 Windows Graphics Capture、DirectX、Imaging、Storage Streams、OCR 与 `WinRT.ActivationFactory`，因此 WinRT 用途适用性已验证；有效许可/接受证据以及 NOTICE/终端用户条款实施仍保持 OPEN。七个非 win-x64 Sherpa runtime package 在该 publish 树中未出现，但 installer container 未解包，仍保持 `EXCLUDED-CONDITIONAL`。
- 当前仓库根的 ignored `artifacts` 是非权威、可变的构建输出，不是只读 `v0.6.0-stage4` tag artifact manifest。当前 `artifacts/publish` 有 539 个文件，Client/Host informational version 绑定 `693719d09cead42304d7c3334b98e9161128c623`，不是正式 C0 `3a591a7b6af7da7d97e07093d4c33a3f44553b82`，因此不能用于 attribution。
- 当前 `artifacts/release` 含一个 V0.6.0 命名的 installer，QA 观察大小为 64,216,176 bytes；它不是正式冻结的 tag-source installer identity，同样不得用于 attribution。
- Stage 4 构建当时没有产出逐文件 manifest；本轮仅从仍保留、且 installer size/SHA-256、tag target 与 publish file count 全部精确匹配的原始 C0 tag-source artifact 生成静态证据。该动作没有重建、修改或执行 artifact，也没有移动 tag。若未来使用 clean rebuild，只能作为“可重建参考”，不能替代本清单绑定的原始 C0 身份。
- `docs/baselines/V0.6.0_STAGE4_C0_PAYLOAD_ATTRIBUTION.json` 对 539 个 `installedPayload` 文件完成离线归属对账：package 43、runtime 470、project-owned 20、build-derived 6、container-only 0、unknown 0；其中 517 条由具体源文件 SHA-256 相等证明，22 条由 exact C0 deps/project publish metadata 证明。42 个实际进入 payload 的普通 package 与 3 个 runtime pack 均有逐文件来源记录。
- 43 个 C0 deps 普通 package contentHash 均与 C0 lock metadata 一致，但当前本机 cache 的 43 个 `.nupkg.sha512` 全部与历史 C0 值不同。因此本证据只把 cache 内**具体文件**的 SHA-256 相等作为 file-level attribution，不把当前 cache package container 冒充为 C0 原包容器，也不据此升级许可结论。

## 10. 相互独立的发布门禁

以下门禁彼此独立，不能互相替代：

1. License/NOTICE/attribution 完整性；
2. 数字签名与私钥安全；
3. 隔离干净 Windows 的 same-AppId install/upgrade/rollback/uninstall 生命周期；
4. tag、version、hash、signature 与 per-file release identity。

任一项缺失都保持 External Distribution=`EXTERNAL_DISTRIBUTION_BLOCKED`。

## 11. 官方来源证据层（既有访问日期 2026-08-30；本批补充记录 2026-08-31）

分类含义：`OFFICIAL_PACKAGE_METADATA_VERIFIED` 仅确认 exact package metadata；`LICENSE_VERIFIED_NOTICE_OPEN` 表示 exact LICENSE 已核、但 NOTICE/third-party notices 尚未取得或闭合；`FULL_LICENSE_NOTICE_VERIFIED` 表示已核对适用的完整 LICENSE 以及存在时的 NOTICE/third-party notices，但仍不自动证明这些文件已布置到冻结产物；`PARTIAL` 表示只有家族、源码、通用条款或部分组件证据；`UNKNOWN` 表示许可绑定仍不足。

| Component / exact version | Official classification | 已核事实与边界 |
|---|---|---|
| Microsoft.Data.Sqlite / Core 10.0.11（2/2） | `LICENSE_VERIFIED_NOTICE_OPEN` | 两个 exact NuGet 页面均确认 MIT；Core 页面明确不含 native binary，当前 exact-version nuspec 指向 dotnet/dotnet commit `e2f47b0110ed922f21a1522da67279133ce28f32`，该 exact commit MIT LICENSE 已核。该 commit 根 THIRD-PARTY-NOTICES 未取得，不能升级为 full，也不代表 NOTICE 已布置。 |
| Microsoft.Extensions.* 10.0.11（本批 24/27 exact-page 目标成功） | `LICENSE_VERIFIED_NOTICE_OPEN` | 27 个 unique C0-mapped package 中 24 个 exact NuGet 页面返回并确认 MIT；当前 exact-version nuspec 均指向 dotnet/dotnet commit `e2f47b0110ed922f21a1522da67279133ce28f32`，其 MIT LICENSE 已核。C0 历史 package container 与当前 cache 不一致，当前 nuspec 只能作为本批 source/license 证据，不能冒充 C0 package-container 证据；NOTICE placement 仍 OPEN。 |
| Microsoft.Extensions.Configuration.FileExtensions / FileProviders.Abstractions / FileProviders.Physical 10.0.11（3/27） | `PARTIAL` | 三个 exact NuGet 页面本批仅得到 Internal Error，不能把其他 24 项的 exact-page 结果扩展到这三项；家族 exact commit LICENSE 证据存在，但 exact package-page metadata 仍未闭合。 |
| System.Speech 10.0.10 | `FULL_LICENSE_NOTICE_VERIFIED` | exact NuGet metadata，以及 dotnet/runtime `v10.0.10` LICENSE.TXT 与 THIRD-PARTY-NOTICES.TXT 已核；不等于冻结产物已布置 NOTICE。 |
| NAudio 2.2.1（7 包） | `FULL_LICENSE_NOTICE_VERIFIED` | exact release `v2.2.1` 与 exact-tag `license.txt` 已核；未发现独立 NOTICE。本结论不改写七个 package/contentHash 行。 |
| SQLitePCLRaw 2.1.12（4 包） | `FULL_LICENSE_NOTICE_VERIFIED` | exact release `v2.1.12` 绑定 `SQLitePCLRaw.lib.e_sqlite3` 到 SQLite 3.53.3；exact native package page 确认包含 native SQLite build 且为 Apache-2.0，exact tag LICENSE.TXT 与 NOTICE.TXT 已核，sqlite.org 官方页确认 SQLite 核心为 public domain。Frozen file attribution 已闭合；package-container mismatch 与 installer NOTICE placement 仍 OPEN。 |
| Sherpa ONNX 1.13.4 / ONNX Runtime 1.27.0 | `FULL_LICENSE_NOTICE_VERIFIED` | 两个 exact NuGet 页面均为 Apache-2.0；release `v1.13.4` 精确指向 commit `142807252687d81b40d6315f23470a1512a00de3`，该 commit LICENSE 已核；release notes 绑定 ONNX Runtime 1.27.0，其 exact tag MIT LICENSE 与 ThirdPartyNotices 已核。Frozen win-x64 file attribution 已闭合，installer NOTICE placement 仍 UNKNOWN；七个非 win-x64 lock entries 仍不代表已分发。 |
| .NET / WindowsDesktop runtime 10.0.11 | `FULL_LICENSE_NOTICE_VERIFIED` | 官方 release notes 绑定 .NET/Windows Desktop/Microsoft.NETCore.App.Runtime.win-x64 10.0.11；dotnet/runtime exact tag `v10.0.11` MIT LICENSE.TXT、THIRD-PARTY-NOTICES.TXT 与 .NET Library Terms 的 object-code redistributable 条件均已核。Frozen runtime files 已逐文件归属；installer LICENSE/NOTICE placement 仍 UNKNOWN。 |
| Microsoft.Windows.SDK.NET.Ref 10.0.19041.57 | `CONDITIONAL_SUPPORTED` | frozen C0 DLL 与本地锁定 exact package 的 SHA-256 完全相同；官方 exact NuGet page 确认 version `10.0.19041.57`、Microsoft/WindowsSDK owner 与 WinSDK license link，官方 REDIST 页面明确列出 package/DLL 并给出有条件再分发许可。Exact C0 源码已证明 WinRT 用途适用性；有效许可或接受证据与 NOTICE/终端用户条款实施仍为 OPEN，因此不是无条件 clearance。 |
| Inno Setup 6 / ChineseSimplified.isl | `PARTIAL` | JR Software `issrc` 默认分支官方许可已核，但实际 compiler 小版本未固定。第三方 `kira-96` 仓库默认分支 MIT 材料与 JR 官方默认分支 `ChineseSimplified.isl` 文件存在性参考均已核；两者都没有证明本地 `ChineseSimplified.isl` 的 exact source commit/hash/provenance，也不能单独建立本地文件的 license binding。 |
| sherpa-onnx-streaming-zipformer-zh-int8-2025-06-30 | `UNKNOWN` | 官方模型说明可核；权重、tokens 与训练数据的明确许可绑定不足，且模型当前不在安装包内。 |

### WinSDK exact file / package / redistribution 状态

| State | Result | Evidence boundary |
|---|---|---|
| `WINSDK_FILE_TO_PACKAGE_BINDING` | `VERIFIED` | Frozen C0 `Microsoft.Windows.SDK.NET.dll`：24,877,600 bytes，file version `10.0.19041.55`，SHA-256 `0EC371D93798852E36461C8ADDDBEADCE0F963A04752F0B64E54FE19C1C834A7`；本地锁定 `Microsoft.Windows.SDK.NET.Ref` 10.0.19041.57 package 中同名 DLL 的 SHA-256 完全一致。文件内部版本与 package 版本不同不再被误写成未绑定。 |
| `WINSDK_OFFICIAL_REDIST_LISTING` | `VERIFIED` | Microsoft 官方 REDIST 页面（updated 2024-10-21）明确列出 `Microsoft.Windows.SDK.NET.Ref` 与 `./lib/net8.0/Microsoft.Windows.SDK.NET.dll`。 |
| `WINSDK_REDISTRIBUTION` | `CONDITIONAL_SUPPORTED` | 官方 REDIST 页面允许按适用许可条款，以未修改 NuGet package 或作为启用 WinRT API 调用的程序组成部分分发；这不是无条件分发许可。 |
| `WINSDK_VALID_LICENSE_OR_ACCEPTANCE_EVIDENCE` | `OPEN` | 通用 Windows SDK license 说明分发取决于有效许可软件及分发要求/限制；本轮没有下载、保存或读取 RTF 附件，也没有建立本项目的有效许可/接受证据。 |
| `WINSDK_NOTICE/END_USER_TERMS_IMPLEMENTATION` | `OPEN` | 当前 publish/installer 尚未完成适用 NOTICE、终端用户条款或相关 placement/验证。 |
| `WINSDK_WINRT_USE_PURPOSE_APPLICABILITY` | `VERIFIED` | Exact C0 `3a591a7b6af7da7d97e07093d4c33a3f44553b82` 的 `src/ScreenGuide.Vision.Windows/WindowsGraphicsCaptureBackend.cs` 使用 Windows.Graphics.Capture、DirectX、Imaging、Storage.Streams 与 `WinRT.ActivationFactory`；`src/ScreenGuide.Vision.Windows/WindowsLocalWindowVisionProvider.cs` 使用 Windows.Graphics.Imaging、Windows.Media.Ocr 与 Windows.Storage.Streams，直接证明该组件用于启用 WinRT API 调用。 |

匿名 Microsoft Q&A 中“may not redistribute”的回答不是权威许可材料，且与官方 REDIST 明确清单冲突；本清单不把它作为 clearance 或 prohibition evidence。

### 官方 URL（只记录，不在本提交访问）

- <https://www.nuget.org/packages/Microsoft.Data.Sqlite/10.0.11>
- <https://www.nuget.org/packages/Microsoft.Data.Sqlite.Core/10.0.11>
- <https://raw.githubusercontent.com/dotnet/dotnet/e2f47b0110ed922f21a1522da67279133ce28f32/LICENSE.TXT>
- <https://licenses.nuget.org/MIT>
- <https://www.nuget.org/packages/Microsoft.Extensions.Hosting/10.0.11>
- <https://github.com/dotnet/core/blob/main/license-information.md>
- <https://www.nuget.org/packages/System.Speech/10.0.10>
- <https://github.com/dotnet/runtime/blob/v10.0.10/LICENSE.TXT>
- <https://github.com/dotnet/runtime/blob/v10.0.10/THIRD-PARTY-NOTICES.TXT>
- <https://github.com/naudio/NAudio/releases/tag/v2.2.1>
- <https://raw.githubusercontent.com/naudio/NAudio/v2.2.1/license.txt>
- <https://github.com/ericsink/SQLitePCL.raw/releases/tag/v2.1.12>
- <https://www.nuget.org/packages/SQLitePCLRaw.lib.e_sqlite3/2.1.12>
- <https://github.com/ericsink/SQLitePCL.raw/blob/v2.1.12/LICENSE.TXT>
- <https://github.com/ericsink/SQLitePCL.raw/blob/v2.1.12/NOTICE.TXT>
- <https://www.sqlite.org/copyright.html>
- <https://www.nuget.org/packages/org.k2fsa.sherpa.onnx/1.13.4>
- <https://www.nuget.org/packages/org.k2fsa.sherpa.onnx.runtime.win-x64/1.13.4>
- <https://github.com/k2-fsa/sherpa-onnx/releases/tag/v1.13.4>
- <https://github.com/k2-fsa/sherpa-onnx/blob/v1.13.4/LICENSE>
- <https://raw.githubusercontent.com/k2-fsa/sherpa-onnx/142807252687d81b40d6315f23470a1512a00de3/LICENSE>
- <https://github.com/microsoft/onnxruntime/blob/v1.27.0/LICENSE>
- <https://github.com/microsoft/onnxruntime/blob/v1.27.0/ThirdPartyNotices.txt>
- <https://dotnet.microsoft.com/en-us/dotnet_library_license.htm>
- <https://github.com/dotnet/core/blob/main/release-notes/10.0/10.0.11/10.0.11.md>
- <https://github.com/dotnet/runtime/blob/v10.0.11/LICENSE.TXT>
- <https://github.com/dotnet/runtime/blob/v10.0.11/THIRD-PARTY-NOTICES.TXT>
- <https://www.nuget.org/packages/Microsoft.Windows.SDK.NET.Ref/10.0.19041.57>
- <https://learn.microsoft.com/en-us/legal/windows-sdk/redist>
- <https://learn.microsoft.com/en-us/legal/windows-sdk/license-terms-ewdk>
- <https://github.com/jrsoftware/issrc/blob/main/license.txt>
- <https://github.com/kira-96/Inno-Setup-Chinese-Simplified-Translation/blob/main/LICENSE>
- <https://github.com/jrsoftware/issrc/blob/main/Files/Languages/ChineseSimplified.isl>
- <https://k2-fsa.github.io/sherpa/onnx/pretrained_models/online-transducer/zipformer-transducer-models.html>

### OpenAI 图标条款证据（两次获授权检索；记录日期 2026-08-31）

本段只离线记录已获授权且经独立审阅的官方 GET 结果；不在本提交重新访问来源，不复制长条款正文：

- <https://openai.com/policies/terms-of-use/> 返回存在辖区/重定向差异：Coordinator 检索观察到 non-EEA/general English-US Terms 变体（发布/生效 2026-01-01）；QA 独立直接 GET 观察到 Europe Terms 变体（更新 2026-01-16），范围为 EEA、Switzerland、UK，并引导其他地区使用另一条款入口。两份公开变体均含 output allocation 条款，同时保留用户权利/合规责任、输出可能不唯一、第三方输出排除、使用前审查及不得把 AI 输出误称为纯人工生成等边界；但本文不判断任一变体适用于 Owner、账户或图标。
- Owner/账户所在地、账户协议与 governing jurisdiction 均未读取或推断；上述检索不选择“正确”变体，也不将任何日期写成适用协议日期。
- <https://openai.com/policies/service-terms/>（更新 2026-06-12）：Codex/code 输出可能受第三方许可约束，但不能自动把该代码条款映射到本图标；视觉输出仍需人物肖像和第三方权利审查，且贸易/商业场景中的商标保护不受保证。
- <https://help.openai.com/en/articles/5008634-will-openai-claim-copyright-over-what-outputs-i-generate-with-the-api>（更新 2026-08-30）：这是 API 补充说明，不证明本地图标 artifact 的生成来源。
- <https://openai.com/policies/sharing-publication-policy/>（更新 2022-11-14）：只作为较早的披露指引，不覆盖或替代 2026 年条款。

因此只记录 `OPENAI_PUBLIC_OUTPUT_TERMS_FOUND=YES`；`APPLICABLE_OPENAI_ACCOUNT_TERMS_NOT_BOUND=OPEN` 与 `OPENAI_OUTPUT_TERMS_CLEARANCE_NOT_ESTABLISHED=OPEN` 继续失败关闭，其他 provenance、披露、copyrightability/uniqueness、第三方权利、商标和外部分发状态亦不变。

### 已关闭的请求审计

- Architect/Security 与 QA 的 package/license 审查、OpenAI 条款审查，以及本批 remaining official LICENSE/NOTICE 核验：均为获授权且限 approved domains 的 GET-only；nonGET=0、downloads=0、ProviderRequests=0、CredentialReads=0。客户端总 GET 数不作为权威字段，三个 Microsoft.Extensions exact 页面仅记录为 Internal Error，未据此补写成功证据。
- 网络证据阶段现已关闭；任何后续在线补证都需要新的 exact-SHA Owner 授权。

## 12. 冻结归属与 NOTICE 合同

### 12.1 四轴与状态词

每个组件必须分别判定四个轴，不能用一个“有许可证”结论替代：

1. **Source**：来源、权利人、版本与适用 LICENSE/NOTICE 是否可绑定；
2. **Bundling**：该组件或文件是否实际进入 C0 installer；
3. **Attribution**：冻结相对路径能否精确映射到组件、版本、package/contentHash 或 C0 blob；
4. **Notice**：适用 LICENSE/NOTICE 是否按合同进入安装包并能从安装文件反查。

状态词固定为：`VERIFIED`（该轴有精确证据）、`PARTIAL`（有证据但绑定未闭合）、`UNKNOWN`（尚未建立）、`VERIFIED-EXCLUDED`（已确认不在当前分发范围，未来纳入需重开 Gate）、`EXCLUDED-CONDITIONAL`（预计排除，但仍需 frozen manifest 的不存在性最终确认）。

| Component boundary | Source | Bundling | Attribution | Notice | 当前结论 |
|---|---|---|---|---|---|
| 仓库源码、文档与非 runtime 品牌材料 | `UNKNOWN` | `VERIFIED-EXCLUDED` | `VERIFIED-EXCLUDED` | `VERIFIED-EXCLUDED` | 不属于当前 installer 产品 payload；Owner 权利人/来源/允许分发形态仍须声明。 |
| 由 C0 项目源码编译的自有 DesktopClient、DesktopHost 与项目 DLL | `PARTIAL` | `VERIFIED` | `PARTIAL` | `UNKNOWN` | 16 个项目 DLL 已由 exact C0 deps project entry 映射，6 个 apphost/deps/runtimeconfig 已标为 build-derived；这不是可复现 source-to-binary hash 证明，Owner 权利边界与 LICENSE/NOTICE 仍未闭合。 |
| runtime Prompt、`.ico` 与删除脚本 | `UNKNOWN` | `PARTIAL` | `PARTIAL` | `UNKNOWN` | manifest 已绑定 publish 树中的 4 个 runtime Prompt；installer 未解包，因此嵌入图标、删除脚本与 container entry 仍未得到同等级逐项证明，Owner 权利边界也未全部闭合。 |
| NAudio 2.2.1 / System.Speech 10.0.10 | `VERIFIED` | `VERIFIED` | `VERIFIED` | `UNKNOWN` | frozen file→exact package/version 的逐文件 SHA-256 归属已闭合，官方许可证据已核；NOTICE 布置仍缺。 |
| Microsoft.Data.Sqlite / Core 10.0.11 | `PARTIAL` | `VERIFIED` | `VERIFIED` | `UNKNOWN` | exact package MIT metadata、exact source commit MIT LICENSE 与 frozen file→package/version 归属已闭合；Source 轴还要求 applicable NOTICE binding，而 exact commit THIRD-PARTY-NOTICES 未取得，因此 Source 保持 PARTIAL、installer NOTICE placement 仍 OPEN。 |
| Microsoft.Extensions application packages 10.0.11 | `PARTIAL` | `VERIFIED` | `VERIFIED` | `UNKNOWN` | 本批 24/27 exact-page 目标确认 MIT，3 个 exact 页面仍 PARTIAL；共同 exact source commit MIT LICENSE 已核，但当前 cache 不是 C0 package container，NOTICE placement 仍 OPEN。 |
| .NET / WindowsDesktop runtime 10.0.11 | `VERIFIED` | `VERIFIED` | `VERIFIED` | `UNKNOWN` | 470 个 runtime 文件已按 exact runtime pack/version 与逐文件 SHA-256 映射；exact release/LICENSE/THIRD-PARTY-NOTICES 与 object-code redistribution 条件已核，installer placement 未闭合。 |
| Sherpa / ONNX Runtime win-x64 | `VERIFIED` | `VERIFIED` | `VERIFIED` | `UNKNOWN` | frozen file→exact package/version 归属、Sherpa exact commit Apache-2.0 LICENSE、ONNX Runtime 1.27.0 MIT LICENSE/ThirdPartyNotices 已核；installer NOTICE placement 仍未闭合。 |
| SQLite managed / native e_sqlite3 | `VERIFIED` | `VERIFIED` | `VERIFIED` | `UNKNOWN` | managed/native frozen files 已按 exact package/version 映射；SQLitePCLRaw exact LICENSE/NOTICE 与 SQLite public-domain 官方证据已核，installer NOTICE placement 仍未闭合。 |
| Inno installer engine / ChineseSimplified.isl | `PARTIAL` | `VERIFIED` | `PARTIAL` | `UNKNOWN` | engine/translation 会进入 installer；compiler exact version 与 translation upstream exact provenance 未闭合。 |
| 中文语音模型 | `UNKNOWN` | `VERIFIED-EXCLUDED` | `VERIFIED-EXCLUDED` | `VERIFIED-EXCLUDED` | 当前 installer 不含模型；未来捆绑/下载必须重新做许可 Gate。 |
| Microsoft.Windows.SDK.NET.Ref 10.0.19041.57 / `Microsoft.Windows.SDK.NET.dll` | `VERIFIED` | `VERIFIED` | `VERIFIED` | `UNKNOWN` | frozen file 与锁定 exact package 的 SHA-256 绑定、官方 exact package page、REDIST listing 与 WinRT 用途适用性均已闭合；再分发仅为 `CONDITIONAL_SUPPORTED`，有效许可/接受与 NOTICE/终端用户条款实施仍 OPEN。 |
| 七个非 win-x64 Sherpa runtime package | `PARTIAL` | `EXCLUDED-CONDITIONAL` | `EXCLUDED-CONDITIONAL` | `EXCLUDED-CONDITIONAL` | 只是 lock-graph entries，且未出现在保留的 539 文件 publish 树；因 installer container 未解包，仍需其不存在性门禁最终关闭。 |

### 12.2 已确认的静态绑定锚点

以下哈希只绑定已说明的本地证据，不能替代原 C0 manifest：

| Evidence | Binding | SHA-256 | 边界 |
|---|---|---|---|
| C0 `ChineseSimplified.isl` | Git blob `30d997321197c7c96d8e111e9ddd6c0ca8da5f09` | `BF0751FA176569C6FAA2F6E17ED2734617BEF325D5CC06EAE030FDD0258EE778` | 仅本地 C0 文件绑定，不证明 upstream exact provenance。 |
| Sherpa managed asset | 本机 package cache 静态文件 | `487B231CCA5B12CC7576E33486B18465DE8C2B2DF3BD04480E9AB0C938DE1FAE` | attribution evidence 已确认同一 SHA-256 存在于 C0 payload；不证明 package container identity 或 NOTICE 已闭合。 |
| Sherpa C API asset | 本机 package cache 静态文件 | `614878147C05121AEB1514EC4FB3E48B89751591532ECA9208235B9AB868306A` | attribution evidence 已确认同一 SHA-256 存在于 C0 payload；不证明 package container identity 或 NOTICE 已闭合。 |
| ONNX Runtime asset | 本机 package cache 静态文件 | `DAA77083A45BF525DA0DDE9E87F85D8EB146F58F9C9AA7124CA84545E1C0F148` | attribution evidence 已确认同一 SHA-256 存在于 C0 payload；不证明 package container identity 或 NOTICE 已闭合。 |
| e_sqlite3 win-x64 asset | 本机 package cache 静态文件 | `B7385D722C83FB52142A00477A726723745916D22A555711EE89834C1111FB2E` | attribution evidence 已确认同一 SHA-256 存在于 C0 payload，source LICENSE/NOTICE 已核；installer placement 与 package-container identity 仍未闭合。 |
| C0 payload attribution evidence | 539 个 frozen payload 文件来源映射 | `C18B2D2F0356F6C8577FE4FD48F82937568F4D67FABF627D26F278F275742FAC` | 539/539 mapped、0 unknown；source manifest 绑定 Git blob `90b1667d9190c92c66982fbd6fa3b0eee091e5c2` 与 `UTF8_NO_BOM_LF_V1` canonical SHA-256，不依赖 checkout 换行；仍只建立 file-level origin evidence，不代表 package container、许可或 NOTICE clearance。 |

### 12.3 Frozen manifest 最小 schema

`docs/baselines/V0.6.0_STAGE4_C0_STATIC_MANIFEST.json` 已按以下 schema 绑定保留的 publish 树与 installer 本体；由于本轮禁止执行或解包 installer，内嵌/派生 container entry 仍为空并作为明确限制保留。任何后续 manifest 仍必须同时描述 installed payload 与 installer container；每条记录至少包含以下字段，不得记录用户绝对路径：

| Field | Contract |
|---|---|
| `artifactScope` | 固定为 `installedPayload` 或 `installerContainer`。 |
| `relativePath` | `installedPayload` 使用相对安装根路径；`installerContainer` 使用 installer 文件的规范化相对路径。与 `artifactScope`、可选 `containerEntry` 共同构成唯一键。 |
| `containerEntry` | 直接安装文件为空；installer 内嵌或构建派生组件记录其稳定逻辑项，例如 Inno engine 或 `ChineseSimplified.isl`。 |
| `size` | 文件字节数。 |
| `sha256` | 文件内容 SHA-256。 |
| `component` / `version` | 归属组件与精确版本。 |
| `componentOrigin` | 组件来源类别及引用，例如 package、C0 Git blob/source 或 build-tool-derived；不得把推测写成来源。 |
| `packageContentHashOrC0Blob` | 可用时记录 package `contentHash` 或 C0 Git blob；不能伪造。 |
| `sourceClassification` | `VERIFIED` / `PARTIAL` / `UNKNOWN` 等本节状态。 |
| `licenseNoticePath` | 安装包内适用 LICENSE/NOTICE 的相对路径；未实现时必须为空并保持阻断。 |
| `bundlingState` | bundled、`VERIFIED-EXCLUDED` 或 `EXCLUDED-CONDITIONAL`。 |
| `confidence` | 证据置信级别及其依据类别，不记录法律结论。 |

若采用两个物理 manifest，则 `installedPayload` manifest 记录安装后的文件树；`installerContainer` manifest 必须记录 installer 自身的 size/SHA-256，并为 Inno engine、编译进 container 的 translation 等 embedded/derived component 建立 `containerEntry` 与 `componentOrigin` 行。两份 manifest 以 installer `relativePath` + `sha256` 绑定，不能只生成 payload manifest 而遗漏 container attribution。

### 12.4 推荐 NOTICE layout

- 安装根设置一个固定第三方通知索引，例如 `THIRD-PARTY-NOTICES.txt`；
- 经确认的许可材料放入 `licenses/<component>/`，保留适用的 `LICENSE`、`NOTICE` 或 `THIRD-PARTY-NOTICES`；
- 索引必须把每个安装文件相对路径映射到 component/version 与对应 license/notice 相对路径；
- 同一许可证不能仅凭家族相似性覆盖不同 package/version；缺失映射必须失败关闭；
- 安装目标 layout 已由 fail-closed 生成器固定：索引进入安装根、bundle manifest 进入 `distribution/`、经验证材料进入 `licenses/<component>/`；当前 skeleton 无合格正文与完整映射，因而不会生成 Inno include，也不会形成实际 installer layout。该技术门禁不是分发许可或法律结论。

`distribution/licenses/bundle-manifest.json` 与 `notice-index.json` 是唯一受版本控制的 bundle/index skeleton；`scripts/Test-DistributionNoticeBundle.ps1` 只在四轴均为 `VERIFIED`、文件/hash/路径/组件与 539 条 payload 映射全部闭合后，原子生成不含通配符和 optional flag 的 Inno 文件清单。`build-desktop-release.ps1` 在任何 restore 或输出目录改动前调用门禁，`ScreenGuideDesktop.iss` 强制 include 该生成文件。当前真实清单保持失败关闭，未执行 build/installer，也未改变 V0.6.0 frozen identity。补齐真实材料、安装后验证与形成新 release identity 仍需单独 S5-R1 授权；不得塞入 S5-R2 或拖到 S5-R4。

### 12.5 Owner Decision Gate

Owner 已作出的事实声明与保守边界如下：

- `OWNER_DECLARATION_RECORDED`：记录第 5 节所述概念启发、无源码复制、无其他已知个人/公司贡献、AI 辅助制作、“元枢”品牌来源和图标生成来源自述；这些内容不冒充仓库验证或法律结论。
- `COMPILED_COMMERCIAL_INTENT_DECLARED`：未来可能销售编译产品和/或提供付费服务，但当前仍是意向，不是分发 clearance。
- `SOURCE_DISTRIBUTION_NOT_AUTHORIZED`：不授权公开分发源码；是否以及如何分发源码仍未决定。
- 品牌名称和图标默认由 Owner 保留；这只是保守临时边界，不等同于选择根许可证，也不改变第三方组件的许可证义务。
- 图标只达到 `OPENAI_PUBLIC_OUTPUT_TERMS_FOUND=YES`；由于辖区/重定向检索得到不同公开变体且没有读取或推断 Owner/账户适用协议，`APPLICABLE_OPENAI_ACCOUNT_TERMS_NOT_BOUND=OPEN`、`OPENAI_OUTPUT_TERMS_CLEARANCE_NOT_ESTABLISHED=OPEN`。
- Owner 来源自述为 `OWNER_PROVENANCE_DECLARED=RECORDED_NOT_INDEPENDENTLY_PROVEN`。
- `AI_DISCLOSURE_REQUIRED=OPEN`、`COPYRIGHTABILITY_AND_UNIQUENESS_NOT_DETERMINED`、`THIRD_PARTY_RIGHTS_REVIEW_REQUIRED=OPEN`、`TRADEMARK_CLEARANCE_NOT_PERFORMED=OPEN`、`ICON_EXTERNAL_DISTRIBUTION_CLEARANCE_NOT_ESTABLISHED=OPEN` 均继续失败关闭。

因此 Owner Decision Gate 只完成了事实声明记录和公开条款变体证据记录；适用 OpenAI 账户条款、图标其他开放状态与现有 S5-R1 blocks 未关闭，不能标为完整 PASS。本文不得推荐或替 Owner 选择根许可证。

### 12.6 新授权门禁

原始 C0 artifact 的只读静态 manifest 已在本轮 Owner 明确授权下完成；其余动作各自仍需要新的、可见的 Owner 授权，不能相互替代：

1. Owner 对图标 AI 披露、第三方权利、商标与外部分发开放项，以及其他尚未闭合资产的补证、替换或排除决定；
2. clean rebuild 及其可重建参考 manifest；
3. NOTICE publish/installer 修改、安装后验证与新 release identity；
4. 任何未来在线许可补证；
5. S5-R2 隔离安装生命周期。

## 13. Remaining blocks

`S5-R1_CONTRACT_PASS` 后仍缺少以下相互独立的证据或实施；本轮不执行：

- 图标仍有 `APPLICABLE_OPENAI_ACCOUNT_TERMS_NOT_BOUND=OPEN`、`OPENAI_OUTPUT_TERMS_CLEARANCE_NOT_ESTABLISHED=OPEN`、`AI_DISCLOSURE_REQUIRED=OPEN`、`THIRD_PARTY_RIGHTS_REVIEW_REQUIRED=OPEN`、`TRADEMARK_CLEARANCE_NOT_PERFORMED=OPEN` 与 `ICON_EXTERNAL_DISTRIBUTION_CLEARANCE_NOT_ESTABLISHED=OPEN`；copyrightability/uniqueness 亦未确定；
- Owner 对 runtime Prompt、删除脚本及其他尚未由本次事实声明闭合资产的来源/允许分发形态补充；源码公开分发继续 `SOURCE_DISTRIBUTION_NOT_AUTHORIZED`；
- installer container 的 embedded/derived entry、publish 树之外的安装文件、LICENSE/NOTICE 适用性与 placement；installer 本体及保留的 539 文件 publish 树身份已由静态 manifest 闭合，539 个 payload 文件的 package/runtime/project/build 来源映射已由 attribution evidence 闭合；
- C0 deps/lock 记录的 43 个普通 package contentHash 与当前本机 cache `.nupkg.sha512` 全部不一致；这不推翻逐文件 SHA-256 归属，但当前 cache 不得作为原 C0 package container identity evidence，后续 package/NOTICE provenance 必须保持失败关闭；
- exact LICENSE/NOTICE 正文、完整 payload/container 映射与全 `VERIFIED` index 尚未补齐；因此实际 publish/installer 布置、安装后验证及新 release identity 均未发生；
- Microsoft.Data.Sqlite/Core exact commit THIRD-PARTY-NOTICES 尚未取得；Microsoft.Extensions 的 Configuration.FileExtensions、FileProviders.Abstractions、FileProviders.Physical 三个 exact package 页面仍为 `PARTIAL`；
- SQLite native `e_sqlite3` 的 installer NOTICE placement；其上游许可/NOTICE source evidence 与冻结 DLL exact package/version 逐文件归属均已闭合；
- Inno compiler exact 小版本及 `ChineseSimplified.isl` 的本地 exact source commit/hash/provenance；
- .NET、WindowsDesktop、Sherpa 与 ONNX Runtime 已核 LICENSE/NOTICE 的 installer 索引和 publish placement；其 source evidence 与 frozen file→component/package 归属已经闭合；
- WinSDK 的 file-to-package binding、官方 REDIST listing 与 WinRT 用途适用性已关闭，但 `WINSDK_VALID_LICENSE_OR_ACCEPTANCE_EVIDENCE=OPEN`、`WINSDK_NOTICE/END_USER_TERMS_IMPLEMENTATION=OPEN`；七个非 win-x64 Sherpa runtime 的 container 不存在性仍待关闭。

WinSDK 已由静态 manifest 证明被捆绑，且 exact file/package binding、官方 REDIST listing 与 WinRT 用途适用性已验证；当前结论固定为 `WINSDK_REDISTRIBUTION=CONDITIONAL_SUPPORTED`，不能写成 unknown、excluded、无条件许可或 release approval。只有有效许可/接受及 NOTICE/终端用户条款实施分别闭合后，WinSDK Gate 才可能放行。七个非 win-x64 Sherpa runtime 仍保持 `EXCLUDED-CONDITIONAL`：若未来 container evidence 发现其被捆绑，或计划捆绑，同样必须重新执行对应的 license/redistribution Gate。

中文语音模型是当前 installer 的 `VERIFIED-EXCLUDED`，不是当前分发包的硬阻断；未来若捆绑或下载，必须重新核对权重、tokens、训练数据许可及 exact model card/source/version/hash。数字签名与 clean-machine same-AppId lifecycle 仍是彼此独立的后续 Gate。

在上述证据和实施补齐并通过独立 Gate 前，不得把 `EXTERNAL_DISTRIBUTION_BLOCKED` 改为可分发。

## 14. 安全与治理

- 本文不包含用户绝对路径、秘密、Prompt 正文、模型内容、原始二进制内容或法律结论。
- 本文是唯一详细 S5-R1 inventory、official evidence 与 attribution/NOTICE contract record；`ROADMAP.md`、`PRODUCT.md`、`MEMORY.md` 与 Charter 只保留摘要和链接。
- 不建立第二套 license registry、数据库或 schema；Module Registry 保持 Shadow，不写 Registry/Lease。
- 本轮文档提交计数：NetworkRequests=0、ProviderRequests=0、CredentialReads=0、InstallerRuns=0、BinaryExecution=0、GUI=0、Microphone=0、Tests=0、Builds=0。第 11 节历史请求审计仅记录已经关闭并通过独立复核的官方来源采集阶段。
