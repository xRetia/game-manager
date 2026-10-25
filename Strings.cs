// Bilingual UI strings. Resolved once from the user's preferred UI language:
// zh* (zh-CN, zh-TW, zh-HK, ...) -> Chinese, anything else -> English.

using System.Globalization;

namespace GameManager;

public static class Strings
{
    private static readonly bool Zh =
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh",
            StringComparison.OrdinalIgnoreCase);

    // ---- chrome ----

    public static string AppTitle => Zh ? "游戏管理器" : "Game Manager";
    public static string BtnRefresh => Zh ? "刷新 [F5]" : "Refresh  [F5]";
    public static string BtnFullScreen => Zh ? "全屏 [F11]" : "Full Screen  [F11]";
    public static string BtnAdvanced => Zh ? "高级" : "Advanced";
    public static string BtnAbout => Zh ? "关于" : "About";

    // ---- input hint (bottom-right) ----

    public static string HintNavigate => Zh ? "移动光标" : "Navigate";
    public static string HintOpen => Zh ? "打开应用" : "Open";

    // ---- library ----

    public static string CountNone => Zh ? "暂无游戏" : "No games";
    public static string CountOne => Zh ? "1 个游戏" : "1 game";
    public static string CountMany(int n) => Zh ? $"{n} 个游戏" : $"{n} games";

    public static string EmptyHint(string folder) => Zh
        ? $"未找到游戏。\n\n请将 .lnk 快捷方式放入：\n{folder}"
        : $"No games found.\n\nAdd .lnk files to:\n{folder}";

    // ---- Advanced menu ----

    public static string MenuRegisterAll => Zh ? "为所有用户注册协议..." : "Register protocol for all users...";
    public static string MenuUnregisterAll => Zh ? "从系统注销协议" : "Unregister protocol from system";
    public static string MenuServiceInstalled => Zh ? "系统服务：已安装" : "System service: installed";
    public static string MenuServiceNotInstalled => Zh ? "系统服务：未安装" : "System service: not installed";
    public static string MenuInstallService => Zh ? "安装系统服务（开机自启）" : "Install system service (auto start)";
    public static string MenuRemoveService => Zh ? "卸载系统服务" : "Remove system service";

    // ---- remote-session GPU (Advanced menu) ----

    public static string RemoteGpuEnabled => Zh
        ? "远程会话 GPU 加速：已启用"
        : "Remote-session GPU acceleration: enabled";
    public static string RemoteGpuDisabled => Zh
        ? "远程会话 GPU 加速：未启用（当前为软件渲染）"
        : "Remote-session GPU acceleration: disabled (currently software rendering)";
    public static string MenuEnableRemoteGpu => Zh
        ? "启用远程会话 GPU 加速（需重连会话）..."
        : "Enable GPU acceleration for remote sessions...";
    public static string MenuDisableRemoteGpu => Zh
        ? "禁用远程会话 GPU 加速"
        : "Disable GPU acceleration for remote sessions";

    public static string MsgRemoteGpuOn => Zh
        ? "已启用远程会话 GPU 加速（硬件优先枚举 + AVC444 + 硬件编码）。\n\n新建会话生效；已连接的座位请重新连接或重启后生效。"
        : "Remote-session GPU acceleration enabled (hardware-first enumeration + AVC444 + hardware encoding).\n\nApplies to newly created sessions; reconnect existing seats or reboot to apply.";

    public static string MsgRemoteGpuOff => Zh
        ? "已禁用远程会话 GPU 加速。\n\n新建会话生效；已连接的座位请重新连接或重启后生效。"
        : "Remote-session GPU acceleration disabled.\n\nApplies to newly created sessions; reconnect existing seats or reboot to apply.";

    // ---- protocol status (Advanced menu, first line) ----

    public static string ProtocolRegisteredAll => Zh ? "steam:// 已注册给所有用户" : "steam:// registered for all users";
    public static string ProtocolRegisteredUser => Zh ? "steam:// 仅当前用户已注册" : "steam:// registered for this user only";
    public static string ProtocolNotRegistered => Zh ? "steam:// 未注册" : "steam:// not registered";

    // ---- messages ----

    public static string MsgRegisteredAll => Zh
        ? "已为所有用户注册 steam:// 协议。"
        : "steam:// protocol registered for all users.";

    public static string MsgNeedAdmin => Zh
        ? "此操作需要管理员权限。\n请在 UAC 提示中确认。"
        : "Administrator rights are required.\nPlease accept the UAC prompt.";

    public static string MsgRemoved => Zh
        ? "已移除 steam:// 协议注册。"
        : "steam:// protocol registration removed.";

    public static string MsgNothingToRemove => Zh
        ? "未找到本程序的注册信息，无需注销。"
        : "No Game Manager registration was found. Nothing to unregister.";

    public static string MsgGameNotFound(string name) => Zh
        ? $"未找到游戏：{name}\n将改为打开游戏管理器。"
        : $"Game not found: {name}\nOpening Game Manager instead.";

    // ---- About ----

    public static string AboutTitle => Zh ? "关于游戏管理器" : "About Game Manager";

    public static string AboutBody(string folder) => Zh
        ? "游戏管理器 2.1.2 (WPF)\n\n" +
          "触控友好的游戏启动器。\n" +
          "游戏快捷方式目录：\n" + folder + "\n\n" +
          "点击游戏选中，再次点击启动（双击也可以）。\n" +
          "方向键 + 回车，或手柄十字键/摇杆 + A/X。\n" +
          "触摸：拖动滚动（带惯性）。\n" +
          "F5 刷新  |  F11 全屏  |  Esc 退出全屏\n\n" +
          "URL 协议：steam://open/bigpicture\n" +
          "直接启动游戏：steam://run/<游戏名>\n" +
          "无边框全屏启动：GameManager --fullscreen\n\n" +
          "使用「高级」菜单管理 steam:// 协议与系统服务。"
        : "Game Manager 2.1.2 (WPF)\n\n" +
          "A touch-friendly game launcher.\n" +
          "Shows shortcuts from:\n" + folder + "\n\n" +
          "Click a game to highlight it, click again to launch (double-click works too).\n" +
          "Arrows + Enter, or gamepad dpad/stick + A/X.\n" +
          "Touch: drag to scroll with inertia.\n" +
          "F5 refresh  |  F11 fullscreen  |  Esc exit fullscreen\n\n" +
          "URL protocol: steam://open/bigpicture\n" +
          "Launch a game directly: steam://run/<Game>\n" +
          "Start borderless with: GameManager --fullscreen\n\n" +
          "Use the Advanced menu to manage the steam:// protocol and the system service.";

    // ---- render diagnostics (About, last line) ----

    public static string DiagRender(string detail) => Zh ? $"渲染：{detail}" : $"Render: {detail}";
    public static string DiagHwOn => Zh ? "硬件加速已启用" : "hardware acceleration on";
    public static string DiagHwDisabled => Zh
        ? "硬件加速已被系统禁用（软件渲染）"
        : "hardware acceleration disabled system-wide (software rendering)";
    public static string DiagRemoteSessionGpuOn => Zh
        ? "远程会话（应用开关已启用；系统 GPU 策略已启用，应使用物理显卡）"
        : "remote session (app switch on; GPU policy on - should be on the physical GPU)";
    public static string DiagRemoteSessionAppSwitchOn => Zh
        ? "远程会话（应用 RDP 硬件加速开关已启用；系统 GPU 策略未启用，会话内可能仍为软件渲染）[可在高级菜单中启用系统策略]"
        : "remote session (app RDP hardware switch on; system GPU policy off - may still be software rendering in this session) [enable the system policy from the Advanced menu]";
    public static string DiagTier0 => Zh
        ? "无可用显卡加速能力（软件渲染）"
        : "no usable GPU acceleration (software rendering)";
    public static string DiagFps(double fps) => Zh ? $"活跃帧率 ~{fps:0} fps" : $"active framerate ~{fps:0} fps";
}
