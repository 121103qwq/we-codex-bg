# we-codex-bg —— 用 Wallpaper Engine 原生渲染做 Codex/ChatGPT 桌面端的动态背景

不解析 `.pkg`，不重写渲染器。Wallpaper Engine 自己用
`wallpaper64.exe -control openWallpaper -playInWindow` 开一个无边框壁纸窗口，
这个 helper 只做窗口层面的事情：找窗口 → 把壁纸窗口塞进 Codex 窗口内部最底层 →
跟随移动/缩放/最大化/最小化/切虚拟桌面 → 退出时原样还原。

色键（`LWA_COLORKEY`）已确认不可行，所以现在走的是**窗口嵌入 + 背景合成**四种模式。

现在有**中文图形界面**（`we-codex-bg-ui.exe`）：壁纸库自动扫描 + 按标题搜索、模式选择、
不透明度滑块、播放/参数控制模块、实时日志。命令行版依然完整保留。

## 安装

```bat
dist\we-codex-bg-setup.exe
```

免管理员权限，装到 `%LOCALAPPDATA%\Programs\we-codex-bg`，可在 Windows「应用和功能」里卸载。
或者直接用免安装包 `we-codex-bg-portable.zip`。

---

## ⚠ 重要：为什么默认模式是 alpha 而不是 composite

如果你遇到过「**壁纸能显示，但 Codex 完全无法操控**」—— 那是 `composite` 模式的问题，已修复。

原因是 Chromium/Electron 宿主（Codex、ChatGPT、VS Code）的内容子窗口
（`Chrome_RenderWidgetHostHWND` / `Intermediate D3D Window`）**本身就带 `WS_EX_TRANSPARENT`**：

```
target=Chrome_WidgetWin_1  pickedChild=Intermediate D3D Window  ex=0x200024
                                                                    └─ 0x20 = WS_EX_TRANSPARENT
```

`composite` 会再给它叠一层 `WS_EX_LAYERED`。**`WS_EX_TRANSPARENT + WS_EX_LAYERED` 会让这个窗口
真正变成"点击穿透"** —— 界面照常绘制，但鼠标事件全部穿过去，于是整个 UI 失去响应；
在实测中甚至直接把渲染进程带崩了（窗口 5/5 次无响应，随后被销毁）。

现在的处理：

1. **拦截**：`composite` 发现内容窗口已带 `WS_EX_TRANSPARENT` 就直接拒绝并降级，不会再触发这个坑。
2. **默认改为 `alpha`**：只对**顶层窗口**加 `WS_EX_LAYERED`，任何宿主都能接受，Codex 保持完全可操作。
   （实测：真实 ChatGPT 窗口在 alpha 模式下 5/5 次保持响应。）
3. **降级顺序**改为 `alpha → overlay → composite → embed`。
4. **旧配置自动迁移**：之前存过 `mode=composite` 的会被一次性改成 `alpha`。
5. **看门狗**：每秒探测宿主是否还响应，连续 3 次无响应就自动还原并退出。
6. **紧急还原热键 `Ctrl+Alt+Shift+W`**：任何时候按下都立即还原所有窗口样式并退出。

---

## 0. 图形界面（推荐）

```bat
build.bat
build-ui.bat
bin\we-codex-bg-ui.exe
```

界面是纯 C# 写的 WPF（无 XAML），用系统自带的 `csc.exe` 编译，零依赖。它本质是
`we-codex-bg.exe` 的启动器：把界面上的选项拼成命令行，把辅助程序作为隐藏子进程拉起来，
再把它的输出实时流进右侧日志面板。

| 界面元素 | 对应参数 |
|---|---|
| **壁纸库**：启动时自动扫描已安装壁纸，按标题/ID/标签搜索，带缩略图 | `--wallpaper` |
| **壁纸路径**（也可拖拽文件进输入框 / 「浏览」） | `--wallpaper` |
| **控制模块**：播放/暂停/停止/静音/音量 | 播放/静音走 `wallpaper64.exe -control ...`；音量只调 Wallpaper Engine 的 Windows 音量会话 |
| **壁纸参数**：读 `project.json` 的 `general.properties` 动态生成控件 | `-control applyProperties` |
| **宿主不透明度**（运行中实时生效） | `--alpha 0-255`（默认 235） |
| **壁纸亮度**（运行中实时生效） | `--wall-alpha 0-255`（默认 255） |
| **模式** 四张卡片：合成 / 透明 / 覆盖 / 嵌入 | `--mode composite\|alpha\|overlay\|embed` |
| **宿主不透明度** 滑块（合成 / 透明模式下显示） | `--alpha 0-255` |
| **壁纸膜不透明度** 滑块（覆盖模式下显示） | `--film 0-255` |
| **目标窗口** 下拉（默认自动检测，可选具体窗口） | `--pid` |
| **高级选项**：WE 路径 / 宿主窗口名 / 内容窗口类名 / 圆角 / 轮询频率 / 三个开关 | `--we` `--we-window` `--content-class` `--round` `--fps` `--full` `--keep-we` `--no-fallback` |
| 底部灰色小字实时显示**最终命令行** | —— |
| **恢复 Codex 窗口** 按钮 | `--restore` |

### 壁纸库是怎么找到的

界面启动时在后台线程扫一遍，不阻塞窗口：

1. **定位 Steam**：先读注册表
   （`HKCU\Software\Valve\Steam\SteamPath`、`HKLM\...\Valve\Steam\InstallPath`），
   再补上 `C:\Program Files (x86)\Steam` 这类常见默认值。
   *Steam 经常不装在 Program Files 下*，所以注册表这一步是必需的 —— 开发这版时的机器
   就把 Steam 装在另一个盘的自定义目录里，只靠原来写死的目录列表一个也匹配不到。
2. **展开所有库**：解析 `steamapps\libraryfolders.vdf` / `config\libraryfolders.vdf`
   里的 `"path"`，把其他盘上的库也收进来。
3. **枚举壁纸**：每个库下扫
   `steamapps\workshop\content\431960\*\project.json`（创意工坊订阅），
   外加 `wallpaper_engine\projects\myprojects` 和 `defaultprojects`（本地/自带壁纸）。
4. **读标题**：`project.json` 里取 `title` / `type` / `preview` / `tags`。
   解析用的是一个几十行的极简 JSON 扫描器（只取顶层字符串），因为 `description`
   字段里常有引号、换行和 `\uXXXX` 转义，简单的正则会被搞挂。
   没有 `title` 的就退回用文件夹名。
5. **缩略图**：`preview.jpg` 按 96px 解码后 `Freeze()`，可以跨线程传回 UI。

搜索框对**标题 + 创意工坊 ID + 标签**做不区分大小写的子串匹配，中英文都行；
下方实时显示「匹配 N / 共 M 张」。选中某一项就把它的 `project.json` 路径填进「壁纸路径」。
扫不到（没装 WE / 装在异常位置）时列表为空，手动填路径的输入框照常可用。

同一套 Steam 查找逻辑也补进了 `we-codex-bg.exe` 的 `FindWeExe()`：
以前它只在 WE **正在运行**时能靠进程路径找到 `wallpaper64.exe`，
WE 没开时那串写死的目录基本必然落空；现在走注册表 + `libraryfolders.vdf`。

### 界面被壁纸冲得发白怎么办

**两条滑块都可以在运行中实时拖动**，不用停下来重开 —— 对着实际效果调才有意义。

| 滑块 | 作用 | 建议 |
|---|---|---|
| **壁纸亮度** `--wall-alpha` | 直接压暗壁纸本身 | **优先调这个**。亮壁纸拉到 100~150，文字对比度立刻回来 |
| **宿主不透明度** `--alpha` | Codex 窗口整体的不透明度 | 拉到 240 以上保证文字清晰 |

原理上「压暗壁纸」比「淡化界面」更划算：淡化界面会同时把文字一起淡掉，而压暗壁纸只动背景。
所以默认宿主不透明度从早期的 205 提到了 **235**（旧配置会自动迁移）。

实现上，UI 直接向 helper 的 message-only 窗口 `PostMessage`（`WM_APP+1` / `WM_APP+2`，
`wParam` 就是 0-255 的 alpha），helper 收到后重新 `SetLayeredWindowAttributes`。
为此壁纸窗口在所有模式下都会被设成 layered（以前只有 overlay 模式才是）。

### 控制模块

一切都通过 Wallpaper Engine 自己的命令行下发，不去碰它的渲染器：

- **播放控制**：`-control play|pause|stop|mute|unmute`。官方命令行没有连续音量命令；
  音量滑块直接调 Windows 音量合成器中 Wallpaper Engine 渲染进程的会话，不会改变 Codex 音量。
- **壁纸参数**：读选中壁纸 `project.json` 里的 `general.properties`，按 `type` 动态生成控件
  （`bool`→复选框、`slider`→滑块、`combo`→下拉、`color`→RGB 输入+色块、`textinput`→文本框；
  `group`/`text` 渲染成分组标题和说明，和 WE 自己的属性面板一致）。
  改动通过 `-control applyProperties -properties RAW~({...})~` 实时下发。
- 标签会把 `ui_browse_properties_scheme_color` 这类本地化 key 还原成可读文字。

`project.json` 的解析用的是自带的 [`src/WeJson.cs`](src/WeJson.cs)（约 200 行，只读）。
没用正则，因为 `description` 字段里常有引号、换行和 `\uXXXX` 转义。
实测覆盖本机 31 张壁纸、505 个可交互参数，0 失败，类型分布与
`ConvertFrom-Json` 的独立统计完全一致。

几个界面层面的细节：

- **停止是安全的**：点「停止」不是直接杀进程，而是给辅助程序的 message-only 窗口发
  `WM_CLOSE`，让它自己走完 `RestoreAll()`；5 秒内没退出才强杀并自动补一次 `--restore`。
  **直接关掉界面窗口也一样**会先把 Codex 窗口还原再退出。
- 运行期间左侧所有选项会锁定，避免改了参数却与实际运行状态不符。
- 选项自动保存在 `%LOCALAPPDATA%\we-codex-bg\ui.cfg`，下次打开自动恢复。

---

## 1. 四种模式

| 模式 | 壁纸窗口位置 | 对 Codex 窗口做的事 | 效果 / 代价 |
|---|---|---|---|
| `composite`（默认） | `SetParent` 成为 Codex 的**子窗口**，永远置于兄弟窗口最底层 | 只给**内容子窗口**（真正画页面那个 HWND）加 `WS_EX_LAYERED + LWA_ALPHA` | 标题栏/窗口框架保持完全不透明，只有页面内容半透，动画从下面透出。移动缩放最小化全部由父窗口带着走，零 z-order 抖动。缺点：内容整体半透，文字也会淡一点 |
| `embed` | 同上（子窗口 + 最底层） | 什么都不改 | 纯嵌入管线验证；宿主如果哪天支持透明背景，这个模式直接就能用。当前宿主背景不透明时看不到画面 |
| `alpha` | 独立顶层窗口，钉在 Codex **正下方一层** | 整个 Codex 窗口 `LWA_ALPHA` | 任何宿主都有效（哪怕它只有一个 HWND）。整窗半透，文字淡得最多 |
| `overlay` | 独立顶层窗口，钉在 Codex **正上方一层**，点击穿透 | **完全不改 Codex 窗口** | 最安全的兜底：动画作为一层半透的膜盖在 UI 上（`--film` 调浓淡）。宿主一旦被 `WS_EX_LAYERED` 弄花/变黑就用这个 |

默认按 `composite → alpha → overlay → embed` 顺序自动降级（API 调用失败才降级；
`--no-fallback` 可关掉）。

调参思路：先 `composite`，`--alpha` 从 205 往下调（越小背景越明显、文字越淡）；
文字受不了就改 `--mode overlay --film 50` 这类，让 UI 完全不动。

---

## 2. 源码结构

```
we-codex-bg\
├─ src\
│  ├─ we_codex_bg.cpp     核心实现，单文件 Win32 C++17（约 700 行）
│  ├─ WeCodexBg.cs        完全等价的单文件 C# 版（P/Invoke 同一套 Win32 API）
│  ├─ WeCodexBgUi.cs      中文图形界面，单文件 WPF（纯 C#，无 XAML）
│  ├─ WeJson.cs           极简只读 JSON 解析器（给 project.json 用）
│  └─ Setup.cs            安装程序，载荷以嵌入资源方式打包
├─ build.bat              build.bat [cpp|cs|auto] -> bin\we-codex-bg.exe
├─ build-ui.bat           ->                         bin\we-codex-bg-ui.exe
├─ build-setup.bat        -> dist\we-codex-bg-setup.exe + portable.zip
├─ run.bat                run.bat "<project.json>" [附加参数]（命令行用法）
└─ README.md
```

`WeCodexBgUi.cs` 的分块：

| 区块 | 作用 |
|---|---|
| `BuildTitleBar / BuildBody / BuildRightPane` | 自绘无边框标题栏（`WindowChrome`）、左侧设置栏、右侧状态+日志+按钮 |
| `BuildWallpaperSection` … `BuildAdvancedSection` | 五个设置分区；每个控件的 change 事件都回调 `UpdateCommandPreview()` |
| `ScanLibraryAsync / ScanLibrary / WallpaperFolders` | 后台线程扫描壁纸库（STA，因为要解码图片） |
| `SteamLibraries / RegString / VdfPaths` | 注册表 + `libraryfolders.vdf` 定位所有 Steam 库 |
| `JsonTopLevelStrings / ReadJsonString` | 极简 JSON 扫描器，只取顶层字符串，正确处理转义 |
| `RenderLibrary / LibraryRow / LibraryItemStyle` | 按搜索词过滤重建列表；深色选中/悬停样式 |
| `ModeCard / UpdateModeVisuals` | 四张模式卡片；切换时同步显示/隐藏对应的滑块 |
| `RefreshTargets` | `EnumWindows` 枚举可见顶层窗口填进下拉框（过滤掉自己和 WE 进程） |
| `BuildArgs / JoinArgs` | 把界面状态拼成命令行参数（只在与默认值不同时才传） |
| `Start / GracefulStop / OnProcExited` | 拉起隐藏子进程并异步读取 stdout/stderr；停止走 `WM_CLOSE` → 超时才强杀 + `--restore` |
| `Card / Input / SecondaryButton / FlatTemplate …` | 一组控件工厂 + 扁平按钮模板，替代 XAML 样式表 |
| `LoadSettings / SaveSettings` | `%LOCALAPPDATA%\we-codex-bg\ui.cfg` 的读写 |

`we_codex_bg.cpp` 内部分块，改起来对着找就行：

| 区块 | 作用 |
|---|---|
| `TopLevelWindows / FindTarget / FindWeWindow / FindWeExe` | 枚举窗口找 Codex 窗口、WE 壁纸窗口；WE 路径优先问正在跑的 WE 进程，其次扫常见 Steam 库 |
| `LaunchWallpaper` | 拼 `-control openWallpaper -file ... -playInWindow ... -borderless` 并 `CreateProcess` |
| `PrepareWallWindow` | 剥掉壁纸窗口的边框/标题栏/任务栏按钮，加 `WS_EX_NOACTIVATE / TOOLWINDOW / TRANSPARENT`，按模式决定 `WS_CHILD + SetParent` 还是 `WS_POPUP` |
| `PickContentChild / ApplyCompositing / MakeLayered` | 找内容子窗口（最大的可见直接子窗口，可用 `--content-class` 指定）并做 alpha 合成 |
| `Sync` | 计算目标客户区矩形 → `SetWindowPos` 同步位置尺寸 + 强制 z-order（子窗口 `HWND_BOTTOM`；顶层模式插在目标上一层/下一层）；矩形和 z-order 都没变就什么都不做 |
| `WinEventProc + SetTimer` | 事件驱动：`EVENT_OBJECT_LOCATIONCHANGE`（移动/缩放）、`EVENT_SYSTEM_FOREGROUND..MINIMIZEEND`（前后台、最小化恢复）、`EVENT_OBJECT_DESTROY`（宿主关闭即退出）；再加 30Hz 定时器兜底 |
| `RestoreAll / CtrlHandler / CrashHandler / RestoreOnly` | 还原：内容子窗口和目标窗口的 `GWL_EXSTYLE`、壁纸窗口的 style/exstyle/父窗口/窗口区域；Ctrl+C、关控制台、注销、未捕获异常都会走到；`--restore` 用来清理被强杀后残留的状态 |

---

## 3. 编译

```bat
cd /d <解压目录>
build.bat
build-ui.bat
```

- `build.bat` 编核心程序 → `bin\we-codex-bg.exe`。
  它会自动挑工具链：找到 MSVC / clang-cl / MinGW-w64 就编 C++ 版，都没有就自动落到
  **C# 版**，用系统自带的 `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`，零依赖。
  `build.bat cpp` / `build.bat cs` 可强制指定。两个版本参数、行为完全一致。
  （C++ 版用 `/utf-8` 编译，因为源码里有中文日志字符串。）
- `build-ui.bat` 编图形界面 → `bin\we-codex-bg-ui.exe`。始终用 `csc.exe` + .NET Framework
  自带的 WPF 程序集（`WindowsBase` / `PresentationCore` / `PresentationFramework` /
  `System.Xaml`），同样零依赖、不需要装任何 SDK 或 NuGet 包。

两个 exe 必须放在同一目录（界面按同目录名字找 `we-codex-bg.exe`）。

---

## 4. 运行（命令行）

前提：Wallpaper Engine 已安装（Steam 版），Codex/ChatGPT 桌面端已经开着。
（不想记参数就用上面第 0 节的图形界面。）

最省事：

```bat
run.bat "C:\Program Files (x86)\Steam\steamapps\workshop\content\431960\<壁纸ID>\project.json"
```

等价的直接调用：

```bat
bin\we-codex-bg.exe --wallpaper "...\project.json" -v
```

想自己先开壁纸窗口再让 helper 接管：

```bat
wallpaper64.exe -control openWallpaper -file "...\project.json" -playInWindow "CodexWallpaperHost" -borderless
bin\we-codex-bg.exe -v
```

**停止：在控制台按 Ctrl+C**（或直接关掉这个控制台窗口）。两种都会走 `RestoreAll()`，
把 Codex 窗口样式和壁纸窗口原样还原；自己启动的壁纸窗口会被关掉（`--keep-we` 可保留）。

常用参数：

```
--mode composite|embed|alpha|overlay
--alpha 0-255        composite/alpha 的宿主不透明度（默认 235）
--film 0-255         overlay 的壁纸不透明度（默认 70）
--wall-alpha 0-255   其他模式下的壁纸亮度（默认 255，调低可压暗壁纸）
--content-class <s>  composite 模式指定要淡化的子窗口类名片段
--title / --class / --exe / --pid    指定目标窗口
--we <wallpaper64.exe 路径>
--we-window <名字>   -playInWindow 用的窗口名，默认 CodexWallpaperHost
--attach-title <s>   按标题片段接管已有的 WE 窗口
--full               覆盖整窗而不只是客户区
--round <px>         给壁纸窗口切圆角（Win11 圆角漏边时用）
--fps <n>            兜底轮询频率，默认 30
--list / --tree / --restore / -v
```

---

## 5. 排错

| 现象 | 处理 |
|---|---|
| `Codex/ChatGPT window not found` | `bin\we-codex-bg.exe --list` 看真实标题/类名/进程名，再用 `--pid 1234` 或 `--title "Codex"` 指定 |
| `未找到 Wallpaper Engine 壁纸窗口`（而且怎么重试都不行） | **已修复**。原因：helper 被强杀后会留下一个**隐藏的** `CodexWallpaperHost` 窗口，WE 从此拒绝再用这个名字开新窗口，而旧版 helper 的窗口枚举又跳过隐藏窗口 —— 于是永久卡死直到重启 WE。现在启动时会自动检测并关掉残留窗口；也可以手动跑 `we-codex-bg.exe --restore` 清理 |
| `Wallpaper Engine window not found`（其他情况） | 确认 WE 在跑；`--we` 指定 `wallpaper64.exe` 全路径；或按上面手动开窗口再 `--attach-title` 接管 |
| composite 模式下看不到动画 | `--tree` 看子窗口树，挑那个铺满客户区的**直接子窗口**（Chromium 系一般是 `Chrome_RenderWidgetHostHWND` 或 `Intermediate D3D Window`），用 `--content-class Chrome_RenderWidgetHostHWND` 指定。注意：`--content-class` 可以匹配任意层级，但如果匹配到很深的后代，它是跟自己那个不透明的祖先做混合，动画照样透不出来 —— 优先选直接子窗口；再不行 `--mode alpha` |
| 加了 layered 之后窗口变黑/花屏 | 宿主用 DirectComposition 直出，被 layered 打断了。用 `--mode overlay`（完全不碰宿主窗口） |
| 壁纸盖住了 Codex 的界面 | 说明 z-order 没压住，`-v` 看日志；`--fps 60` 提高兜底频率 |
| 帮助程序被任务管理器强杀后 Codex 窗口不正常 | `bin\we-codex-bg.exe --restore`（剥掉残留的 `WS_EX_LAYERED`，把还挂在 Codex 里的 WE 窗口踢回桌面）；界面上就是「恢复 Codex 窗口」按钮 |
| Win11 圆角处露出方角 | `--round 8`（界面：高级选项 → 圆角） |
| 界面提示「未在本程序旁找到 we-codex-bg.exe」 | 先跑 `build.bat`；两个 exe 必须在同一个 `bin\` 目录里 |
| 界面能开但点启动没反应 | 看右侧日志面板，它就是命令行版的完整输出；照上面几行对症处理 |
| 壁纸库列表是空的 | 确认 WE 是 Steam 版且订阅过壁纸；点「重新扫描」；实在扫不到就在「壁纸路径」里手动填 `project.json` 全路径 |
| 列表里少了某些壁纸 | 只扫 `workshop\content\431960`、`myprojects`、`defaultprojects` 三处；放在别处的用手动路径 |

---

## 6. 已知限制

- 没有虚拟显示器，也没有改 WE 渲染器 —— 就是原生窗口 + 窗口关系。
- 三种可见模式都是**整层 alpha 合成**，做不到"文字 100% 不透明、只有背景透"：那需要宿主自己
  按像素输出 alpha（或宿主提供透明背景/自定义 CSS），Windows 层面没有第三条路
  （色键已试过不行）。所以只能用 `--alpha` / `--film` 在"背景明显"和"文字清晰"之间取平衡。
- `composite/embed` 依赖宿主有独立的内容子窗口。宿主如果是单 HWND 直出，自动降级到 `alpha`。
- 子窗口会随父窗口一起销毁，所以嵌入模式下 helper 监听了目标窗口的
  `EVENT_OBJECT_HIDE / DESTROY`：你一关（或最小化到托盘）Codex 窗口，它会**立刻先还原**
  再退出，避免壁纸窗口被连带干掉。想再挂上就重新跑一次 `run.bat`。
- 壁纸窗口一律 `WS_EX_TRANSPARENT + WS_EX_NOACTIVATE`，不吃鼠标事件，不会抢焦点。
- 多显示器/DPI：进程声明 Per-Monitor-V2，坐标和目标窗口 1:1；跨屏拖动会跟随。
- 高负载壁纸依然是 GPU 开销，跟 WE 正常跑一张壁纸一样。
