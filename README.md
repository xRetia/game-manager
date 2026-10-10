# GameManager

多座位（Multi-Seat）游戏启动器：触摸友好的深色现代化界面，通过 `steam://open/bigpicture` 协议在任意用户会话中呼出，列出 `C:\MultiSeatGames\Links` 下的所有游戏快捷方式并可一键启动。支持键盘、手柄、触摸三种操作方式。

## 架构

```
steam://open/bigpicture
        │
        ▼
GameManager.exe --open <sessionId>   （协议处理器，HKLM 全局注册）
        │  命名管道: SHOW <sessionId>
        ▼
GameManagerService（Windows 服务，SYSTEM，开机自启）
        │  WTSQueryUserToken + TokenLinkedToken（用户的管理员提升 token）
        │  CreateProcessAsUser --show <sessionId>
        ▼
GameManager UI（目标用户会话内运行，单实例/已有实例则前置激活）
```

- **绝不用 SYSTEM 权限跑 UI**：服务通过 WTS 拿用户提升 token 后在目标会话内启动界面进程。
- **单实例**：每用户一个命名互斥体；重复呼出时经管道发 `ACTIVATE`，已有窗口前置，不开新窗口。
- **前台抢焦**：服务拉起的进程没有前台权限，内置 AttachThreadInput + Topmost 轮询夺回键盘焦点。

## 功能

- 自适应网格布局：大屏 256px 图标顶格，小屏自动缩列
- GPU 亚克力背景：选中游戏图标高斯模糊（Radius=120）+ 深色遮罩交叉淡入
- 交互：单击选中高亮、再击启动（双击直启）；方向键 + 回车；手柄十字键/左摇杆导航、A/X 确认
- 平滑滚动：仅当选中项越界时以最小位移动画滚入，触摸惯性滚动带边界反馈
- F5 刷新、F11 无边框全屏（覆盖任务栏）、Esc 退出
- 入场交错动画、悬停弹性缩放、选中辉光等动效

## 构建

要求 .NET 9 SDK（`net9.0-windows`，UseWPF）。

```
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

产物：`publish\GameManager.exe`（单文件，约 207KB）+ 自动生成的 `GameManager.json` 配置。

## 配置

`GameManager.json`（与 exe 同目录）：

| 键 | 说明 |
|---|---|
| `LinksFolder` | 游戏快捷方式目录，默认 `C:\MultiSeatGames\Links` |
| `StartFullScreen` | 呼出时是否直接全屏（`--fullscreen` 参数优先） |

## 部署

界面的 Advanced 菜单可管理（均自动 UAC 提权）：

- Register / Unregister protocol：`steam://` 协议的 HKLM 全局注册
- Install / Remove system service：`GameManagerService` 系统服务（开机自启）

## 调试流程

⚠️ 服务与 UI 都会锁定 exe，重新编译前必须：

1. 管理员执行 `taskkill /f /im GameManager.exe` + `sc stop GameManagerService`（一次 UAC）
2. `dotnet publish ...`
3. `sc start GameManagerService`（再一次 UAC）
4. 浏览器/运行框访问 `steam://open/bigpicture` 呼出测试

## 源码文件

| 文件 | 职责 |
|---|---|
| `App.xaml(.cs)` | 全局样式（按钮/瓦片/深色菜单）与模式分发入口 |
| `MainWindow.xaml(.cs)` | 主界面、键盘/手柄/触摸/滚动/焦点逻辑 |
| `ServiceHost.cs` | SCM 服务宿主、管道监听、WTS token 会话内启动 |
| `Launcher.cs` | 协议注册/反注册、提权执行、配置目录 |
| `SingleInstance.cs` | 每用户命名互斥体 + 激活已有实例 |
| `GameInfo.cs` | 游戏数据模型（名称/图标/选中态） |
| `GamePad.cs` | winmm joyGetPosEx 手柄轮询 |
| `ShellIcons.cs` | .lnk Shell 图标提取（256px 大图标） |
| `Config.cs` | GameManager.json 读写 |
