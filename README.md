# DeepDive

一个用 C# + WPF 写的 Minecraft Java 版启动器，只支持 Windows。

> 作者：**hey-ylp**（需求、设计、验收） · **DeepSeek**（代码）

我没打算做成 PCL / HMCL 的替代品，只是想把日常用得到的那些功能做成自己能控制的形状：安装、启动、装模组、看日志，不夹带广告和捆绑。

**直接用：** [下载最新版（GitHub Releases）](https://github.com/hey-ylp/deepdive-launcher/releases/latest) — 解压后双击 `DeepDive.exe`，不用装 .NET。想看代码或自己编译，往下翻。

![首页](docs/screenshots/home.png)

## 关于这个项目

**代码由 DeepSeek 生成**（DeepSeek-V4.1-Flash），我负责提需求、定方案、验收结果、以及在真机上踩坑排错。

写在这里不是免责声明，是想让你看代码时有个预期：

- 注释非常密，大部分是踩坑记录（“为什么不能这么写”），因为改的人不看就会再踩一遍
- 结构是边做边长出来的，不是一开始设计好的，个别地方有历史包袱
- 你说得出来的功能都真机验证过，但边界情况不一定覆盖到了

发现问题欢迎提 issue，不过回不回、什么时候改，看我当时有没有空。

## 功能

**游戏安装与启动**

- 原版全版本下载与启动，支持快照
- Forge / NeoForge / Fabric 一键安装（版本列表从 maven 元数据拉，不爬网页）
- 下载源可切：官方源 / BMCLAPI 镜像，切换后自动测速
- 多实例隔离：每个实例独立的 `mods` / `saves` / 配置，共享 `libraries` / `assets` / `runtime`
- Java 运行时按需自动下载，也可指定本地 JDK
- 内存、GC、自定义 JVM 参数

**内容**

- 直接搜 **Modrinth** 或 **CurseForge** 装模组、整合包、光影包、资源包，自动解析前置依赖
  - CurseForge 走 MCIM 公益镜像，不需要自己的 API key
  - CurseForge 的前置依赖按「当前游戏版本 + 加载器」自动匹配
  - CurseForge 的整合包也能直接装（读 manifest.json，新建独立实例）
- 导入本地整合包（`.mrpack`、CurseForge `.zip`、手动打包的懒人包）
- 模组管理：中文名、图标、启停开关
- 启动前模组预检：查缺前置、重复安装、加载器不匹配
- 模组界面汉化：用 DeepSeek 翻译，译文打成资源包，不动原 jar

**账号**

- 离线模式（UUID 由名字算 MD5 得出，换启动器不会丢存档）
- 微软正版登录，refresh token 自动续期，不用反复登
- 可导入官方启动器里已登录的令牌

**其他**

- 崩溃日志分析：按规则定位原因，也可以接 AI 再分析一层
- 文件完整性校验，坏了能单独重下
- 种子查询：直接从存档文件读，不用进游戏按 F3
- 联机：基于 EasyTier 组虚拟局域网（需自行下载组件，见下文）

## 截图

| 版本列表 | 内容搜索 |
|---|---|
| ![版本](docs/screenshots/versions.png) | ![内容](docs/screenshots/content.png) |

| 联机 | 工具 |
|---|---|
| ![联机](docs/screenshots/lianji.png) | ![工具](docs/screenshots/tools.png) |

![设置](docs/screenshots/settings.png)

## 环境要求

- Windows 10 / 11（x64）
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)（自己编译才需要，用现成 exe 不用）

## 构建

```powershell
git clone <仓库地址>
cd mc-launcher
dotnet build DeepDive.sln -c Release
```

打包成单个 exe：

```powershell
dotnet publish src/DeepDive.App/DeepDive.App.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -o dist
```

产物在 `dist\`，约 170 MB（自包含运行时，目标机器不用装 .NET）。

## 联机组件（可选）

联机功能依赖 [EasyTier](https://github.com/EasyTier/EasyTier)（LGPL-3.0）。这 34 MB 二进制没有放进仓库，需要自己下。

1. 从 [EasyTier Releases](https://github.com/EasyTier/EasyTier/releases) 下载 **`easytier-windows-x86_64-v2.6.4.zip`**（当前适配 2.6.4，其他版本 CLI 参数可能变）
2. 解压后把下面这些文件放到 `vendor/easytier/`：

```
vendor/easytier/
├── easytier-core.exe      ← 压缩包里有
├── easytier-cli.exe       ← 压缩包里有
├── wintun.dll             ← 压缩包里有
├── Packet.dll             ← 压缩包里有
├── WinDivert64.sys        ← 压缩包里有
└── LICENSE-LGPL-3.0.txt   ← 压缩包里没有，从 EasyTier 仓库根目录取
```

压缩包里另外那两个 `easytier-web*.exe` 用不到，不用放。

不放也能编译，只是联机页用不了。

## 账号与隐私

启动器**不向任何自建服务器上传数据**，所有状态都在本地 `instances\` 和 `launcher\` 里。

**微软登录**用的是 Xbox Live 的公共应用 ID（`000000004C12AE6F`），不是私有密钥，也没有任何密钥需要配置。登录拿到的是微软官方签发的令牌，启动器只负责保存和续期。

**关于「导入官方启动器账号」**：这个功能会读本机 `.minecraft\launcher_accounts.json` 或者 `cmdkey` 里存的令牌，目的是帮你省掉重新登录一次。它只在你主动点那个按钮时执行，读到的令牌只写进本地 `launcher\accounts.json`。不想要这个功能可以整块删掉，不影响其他。

**注意**：`launcher\` 目录里有你的令牌和可选的 DeepSeek API Key，备份或分享文件夹时别整个发出去。这个目录已经在 `.gitignore` 里。

## 目录结构

```
src/
├── DeepDive.App/     WPF 界面层（窗口、页面、视图模型）
└── DeepDive.Core/    无 UI 依赖的核心逻辑
    ├── Auth/           离线 / 微软账号
    ├── Launcher/       路径、设置、崩溃分析
    ├── Modrinth/       内容搜索与下载
    ├── Mods/           模组管理、预检、中文名
    └── Translation/    模组汉化
```

运行时数据（实例、账号、日志）在 exe 同级的 `instances\` 和 `launcher\` 下，都不入库。

## 第三方

| 组件 | 用途 | 协议 |
|---|---|---|
| [CmlLib.Core](https://github.com/CmlLib/CmlLib.Core) | Minecraft 安装与启动 | MIT |
| [WPF-UI](https://github.com/lepoco/wpfui) | Fluent 风格控件 | MIT |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | MVVM | MIT |
| [EasyTier](https://github.com/EasyTier/EasyTier) | 联机组网（可选） | LGPL-3.0 |

## 已知限制

- 只支持 Windows，WPF 的限制
- 只接了 Modrinth 和 CurseForge，没接 FTB / ATLauncher 那些
- CurseForge 完全依赖 MCIM 镜像（个人申请不到 CurseForge 的 API key，只能 Google 账号登录申请），镜像挂了这条线就断
- 种子查询是从存档文件反解，不是查在线地图，所以只能查自己去过的地方
- 界面只有浅色

## 免责声明

个人项目，与 Mojang Studios / Microsoft 没有关联。Minecraft 是 Mojang 的商标。

本启动器不内置游戏本体，也不绕过正版验证，请使用自己合法拥有的账号。

## 许可证

[MIT](LICENSE)。

简单说：随便用、随便改、随便商用，保留版权声明就行。不提供任何担保，出了问题自己负责。
