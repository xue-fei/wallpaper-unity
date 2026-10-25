# 桌面壁纸在 Ubuntu 26.04 上的实现方案

> 目标：把当前 Windows 版 Unity 壁纸（嵌入桌面层 + 全局鼠标转发）移植到 Ubuntu 26.04。
> 状态：方案阶段，尚未动代码。

## 一、结论

| 路线 | 可行性 | 说明 |
|---|---|---|
| **A. X11 + EWMH 桌面窗口类型**（推荐） | ✅ 可行，与现 Windows 设计同构 | Ubuntu 26.04 登录时选「Ubuntu on Xorg」即可 |
| B. KDE Plasma 壁纸插件 | ⚠️ 仅 KDE，且无法直接托管 Unity 渲染面 | 本质还是 X11 技巧的变体 |
| C. Wayland（默认） | ❌ 第三方 Unity 程序无法成为壁纸 | 合成器垄断层级，无公开协议 |

**核心判断**：Ubuntu 26.04 默认 Wayland，必须先切到 X11 会话，否则方案 C 直接堵死。

## 二、路线 A 详细方案（与现架构一一对应）

现 Windows 架构是：`SetParent(WorkerW)` 把 Unity 嵌到桌面层 + `WH_MOUSE_LL` 全局钩子转发鼠标 → Unity。X11 下有完全对等的原语：

### 1. 成为桌面背景
启动 Unity Linux 构建后，用 Xlib 设置窗口属性：
- `_NET_WM_WINDOW_TYPE_DESKTOP`（EWMH 提示，让合成器把它当背景层，置于图标之下）
- 几何铺满根窗口（X11 根窗口天然跨所有显示器，多屏无需额外处理）
- 用 `XLowerWindow` 压到最底

### 2. 鼠标转发（决定能否“可点击”，即保留按钮/双击/hover）
- 用 **X Record 扩展**全局拦截鼠标（等价于 `WH_MOUSE_LL`，不消费事件，桌面图标仍可点）
- 喂给 Unity 的现有 `WallpaperClickQueue` + `WallpaperInputModule`，**这套逻辑基本原样复用**
- 坐标：X11 根坐标是左上原点 Y 向下，Unity 是左下原点 Y 向上 → 沿用已修的 `py = height - py` 翻转思路

### 3. 构建与自启
- Unity 出 Linux x64 包（OpenGL/Vulkan，已有关掉 Unity 闪屏的 `SkipSplash` 可直接用）
- 用一个轻量启动器（C 小程序或 Python-xlib）做“启动 Unity → 找窗口 → 设桌面类型 → 持续保活重设属性”（类 xwinwrap 思路）
- 自启用 `~/.config/autostart/*.desktop` 或 systemd user service

## 三、现有代码复用 / 改动范围（将来做时）

- **直接复用**：`WallpaperInputModule.cs`、`WallpaperClickQueue.cs`、`WallpaperDemoUI.cs`、`SkipSplash.cs`
- **抽象**：把 `WallpaperController` 里 Win32 专属的挂接/钩子抽成 `IWallpaperPlatform` 接口：`AttachToDesktop()` / `GetVirtualScreen()` / `CaptureInput()`
  - Windows 实现保留 `User32.cs` 现有逻辑（`#if UNITY_STANDALONE_WIN`）
  - 新建 `Assets/Scripts/Wallpaper/X11.cs`（Linux Xlib/XCB P/Invoke）+ `LinuxAttach.cs`（`#if UNITY_STANDALONE_LINUX`）
- **坐标翻转**：X11 与 GDI 同为 Y 向下，现有翻转逻辑可平移，无需推倒

## 四、关键风险 / 前提

- **必须 X11 会话**：Wayland 下整条路线失效，启动器需检测 `WAYLAND_DISPLAY` 并提示切 Xorg
- **X Record 扩展**：多数发行版默认有，但容器/精简环境可能缺
- **属性保活**：Unity 可能重设窗口属性，启动器需周期性重设（xwinwrap 也是这么干的）
- **交互取舍**：若只要“纯动态壁纸、不需要点按钮”，可省掉 X Record 转发，复杂度骤降；要保留交互则必须做转发

## 五、建议的 PoC 验证顺序

1. X11 会话下，出个最小 Unity 场景（转个方块即可）的 Linux 包
2. 写 Python-xlib 脚本把它设为桌面类型、铺满、压底，确认出现在图标后面
3. 接 X Record 转发鼠标，验证按钮/双击/hover 与 Windows 表现一致

## 六、待拍板的两个点

- **桌面环境**：默认 GNOME(X11) 还是 KDE？两者都用路线 A，但 KDE 可借 `KWin` 脚本更省事
- **要不要可交互**：保留点击/双击（要做 X Record 转发），还是只要动态背景（可省掉最难的一块）

确认这两点和“走路线 A”后，再动手拆 `IWallpaperPlatform` 并加 Linux 分支。
