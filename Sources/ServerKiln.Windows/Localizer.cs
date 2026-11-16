using System.ComponentModel;
using System.Globalization;

namespace ServerKiln.Windows;

public sealed class Localizer : INotifyPropertyChanged
{
    public static Localizer Current { get; } = new();
    public event PropertyChangedEventHandler? PropertyChanged;
    public bool Chinese { get; private set; } = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh";
    public void SetLanguage(string language)
    {
        Chinese = language == "zh" || language == "system" && CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh";
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }
    public string this[string key] => Labels.TryGetValue(key, out var value) ? Chinese ? value.Zh : value.En : key;
    public string Activity(ServerActivity? activity) => activity switch
    {
        ServerActivity.Busy => this["Busy"], ServerActivity.Idle => this["Idle"],
        ServerActivity.Offline => this["Offline"], ServerActivity.CpuOnly => this["CpuOnly"], _ => this["Connecting"]
    };
    private static readonly Dictionary<string, (string En, string Zh)> Labels = new()
    {
        ["Tagline"] = ("Your compute, at a glance.", "扫一眼，掌握整组服务器。"),
        ["Fleet"] = ("SERVERS", "服务器"), ["Add"] = ("+ Add server", "+ 添加服务器"),
        ["Import"] = ("Import SSH config", "导入 SSH 配置"), ["Refresh"] = ("Refresh", "立即刷新"),
        ["Settings"] = ("Settings", "设置"), ["Edit"] = ("Edit", "编辑"), ["Remove"] = ("Remove", "移除"),
        ["Save"] = ("Save", "保存"), ["Cancel"] = ("Cancel", "取消"), ["Name"] = ("Display name", "显示名称"),
        ["Target"] = ("SSH alias or user@host", "SSH 别名或 user@host"), ["Port"] = ("Port (optional)", "端口（可选）"),
        ["Enabled"] = ("Monitor this server", "启用监控"), ["Muted"] = ("Mute this server", "静音此服务器"),
        ["Busy"] = ("Busy", "忙碌"), ["Idle"] = ("Idle", "空闲"), ["Offline"] = ("Offline", "离线"),
        ["CpuOnly"] = ("CPU only", "纯 CPU"), ["Connecting"] = ("Connecting…", "连接中…"), ["Disabled"] = ("Paused", "已暂停"),
        ["Overview"] = ("Overview", "资源总览"), ["History"] = ("History", "资源历史"),
        ["Processes"] = ("PROCESSES", "进程"), ["Jobs"] = ("RECENT JOBS", "近期任务"),
        ["NoServers"] = ("Add your first server", "添加第一台服务器"),
        ["Welcome"] = ("Import an existing OpenSSH configuration, or add a host manually.\nFirst confirm its fingerprint and unlock your key in a terminal.", "导入现有 OpenSSH 配置，或手动添加服务器。\n请先在终端确认主机指纹，并解锁密钥。"),
        ["NoSelection"] = ("Select a server to see its resources.", "选择服务器，查看资源情况。"),
        ["NoData"] = ("Waiting for a successful sample.", "等待首次成功采集。"),
        ["NoHistory"] = ("History is recorded once a minute. Leave monitoring running to build a timeline.", "历史每分钟记录一次；保持监控运行后即可查看曲线。"),
        ["GpuUse"] = ("GPU UTILIZATION", "GPU 利用率"), ["Vram"] = ("GPU MEMORY", "显存"),
        ["Cpu"] = ("CPU", "CPU"), ["Ram"] = ("MEMORY", "内存"), ["Disk"] = ("ROOT DISK", "根磁盘"),
        ["Network"] = ("NETWORK", "网络"), ["Command"] = ("Command", "命令"), ["User"] = ("User", "用户"),
        ["Elapsed"] = ("Elapsed", "运行时间"), ["Started"] = ("Started", "开始时间"), ["Ended"] = ("Ended", "结束时间"),
        ["Running"] = ("Running", "运行中"), ["Gpu"] = ("GPU", "GPU"), ["Cwd"] = ("Working directory", "工作目录"),
        ["Copy"] = ("Copy command", "复制命令"), ["OpenSsh"] = ("Open SSH terminal", "打开 SSH 终端"),
        ["General"] = ("MONITORING", "监控设置"), ["BackgroundInterval"] = ("Background interval (seconds)", "后台刷新间隔（秒）"),
        ["ForegroundInterval"] = ("Window open interval (seconds)", "窗口打开时刷新间隔（秒）"),
        ["UtilThreshold"] = ("Busy utilization threshold (%)", "忙碌利用率阈值（%）"),
        ["MemoryThreshold"] = ("Busy GPU memory threshold (MiB)", "忙碌显存阈值（MiB）"),
        ["Debounce"] = ("Consecutive observations to confirm a change", "状态切换所需连续采样次数"),
        ["Retention"] = ("Keep history (days)", "历史保留天数"), ["NotifyIdle"] = ("Notify when idle", "空闲时通知"),
        ["NotifyBusy"] = ("Notify when busy", "忙碌时通知"), ["NotifyConnection"] = ("Notify on offline / recovery", "离线或恢复连接时通知"),
        ["Login"] = ("Start at sign-in", "登录 Windows 时启动"), ["SshConfig"] = ("SSH config file (blank = default)", "SSH 配置文件（留空使用默认路径）"),
        ["SshExecutable"] = ("ssh.exe (blank = Windows OpenSSH)", "ssh.exe 路径（留空使用系统 OpenSSH）"),
        ["Browse"] = ("Browse…", "浏览…"), ["Phone"] = ("IPHONE NOTIFICATIONS", "iPhone 通知"),
        ["PhoneHelp"] = ("Install Bark on your iPhone and copy its device key below. This sends status changes through your selected Bark server. Keep this PC awake and connected for notifications.", "在 iPhone 上安装 Bark，将设备密钥填入下方。状态变化将通过所选 Bark 服务推送；电脑需保持唤醒并连接网络。"),
        ["BarkEnabled"] = ("Send notifications to iPhone via Bark", "通过 Bark 向 iPhone 推送"),
        ["BarkServer"] = ("Bark HTTPS server", "Bark HTTPS 服务地址"), ["BarkKey"] = ("Device key (encrypted for this Windows account)", "设备密钥（使用当前 Windows 账户加密保存）"),
        ["BarkTest"] = ("Test iPhone notification", "测试 iPhone 通知"), ["TestSent"] = ("Test notification accepted by Bark.", "Bark 已接受测试通知。"),
        ["PhoneLimit"] = ("The original ServerKiln iPhone app, widgets and iCloud pairing require the official Apple integration. They are not connected by this Windows build.", "原版 ServerKiln iPhone App、小组件和 iCloud 配对依赖官方 Apple 集成，本 Windows 版本尚未接入。"),
        ["Saved"] = ("Settings saved.", "设置已保存。"), ["ImportEmpty"] = ("No new explicit hosts found in this config.", "配置中没有可导入的新主机。"),
        ["RemoveConfirm"] = ("Remove this server and its local history? Your SSH configuration and remote authorization are retained.", "移除此服务器及其本地历史？SSH 配置和远端授权会保留。"),
        ["Error"] = ("ServerKiln needs attention", "ServerKiln 需要处理"), ["Show"] = ("Open ServerKiln", "打开 ServerKiln"),
        ["Quit"] = ("Quit", "退出"), ["TrayHint"] = ("Closing the window keeps monitoring in the system tray.", "关闭窗口后继续在系统托盘监控。"),
        ["Demo"] = ("Demo · synthetic data", "演示模式 · 虚构数据"), ["Local"] = ("SSH · local history", "SSH · 本地历史"),
        ["RetryHint"] = ("First run ssh in a terminal to confirm the host fingerprint and unlock your key. Platform-specific ProxyCommand programs must be available on Windows.", "请先在终端运行 ssh，确认主机指纹并解锁密钥；ProxyCommand 使用的程序需能在 Windows 上运行。"),
        ["24h"] = ("24 hours", "24 小时"), ["7d"] = ("7 days", "7 天"), ["30d"] = ("30 days", "30 天"),
        ["ChartLegend"] = ("GPU use  ·  GPU memory  ·  CPU  ·  RAM", "GPU 利用率  ·  显存  ·  CPU  ·  内存"),
        ["NoGpu"] = ("No NVIDIA GPU detected", "未检测到 NVIDIA GPU"), ["Sampling"] = ("Collecting…", "正在采集…"),
        ["LastSample"] = ("Last sample", "最近采集"), ["Uptime"] = ("Uptime", "运行时长"),
        ["SelectImport"] = ("Select hosts to import", "选择要导入的服务器"), ["SelectAll"] = ("Select all", "全选"),
        ["ImportSelected"] = ("Import selected", "导入所选"), ["NoJobs"] = ("No training jobs recorded yet.", "暂无训练任务记录。")
    };
}
