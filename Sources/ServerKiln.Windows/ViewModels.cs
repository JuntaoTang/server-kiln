using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ServerKiln.Windows;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    protected void AllChanged() => Changed("");
}

public sealed class FleetViewModel : ObservableObject
{
    public ObservableCollection<ServerViewModel> Servers { get; } = [];
    private ServerViewModel? selected;
    public ServerViewModel? SelectedServer
    {
        get => selected;
        set { selected = value; Changed(); Changed(nameof(DetailVisibility)); SelectionChanged?.Invoke(); }
    }
    public event Action? SelectionChanged;
    private string message = "";
    public string StatusMessage { get => message; set { message = value; Changed(); } }
    public Visibility EmptyVisibility => Servers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DetailVisibility => SelectedServer is null ? Visibility.Collapsed : Visibility.Visible;
    public string FleetSummary => $"{Servers.Count(s => s.Reading.Activity == ServerActivity.Busy && s.Config.Enabled)} / {Servers.Count(s => s.Config.Enabled)} {Localizer.Current["Busy"]}";

    public void Sync(AppPreferences preferences)
    {
        var selectedId = selected?.Config.Id;
        foreach (var item in Servers.Where(s => !preferences.Servers.Any(c => c.Id == s.Config.Id)).ToArray()) Servers.Remove(item);
        foreach (var config in preferences.Servers)
        {
            var item = Servers.FirstOrDefault(s => s.Config.Id == config.Id);
            if (item is null) Servers.Add(new ServerViewModel(config)); else item.SetConfig(config);
        }
        SelectedServer = Servers.FirstOrDefault(s => s.Config.Id == selectedId) ?? Servers.FirstOrDefault();
        AllChanged();
    }

    public void Apply(ServerReading reading)
    {
        var server = Servers.FirstOrDefault(s => s.Config.Id == reading.Server.Id);
        server?.Apply(reading);
        Changed(nameof(FleetSummary));
    }
    public void RefreshLanguage() { foreach (var server in Servers) server.Refresh(); AllChanged(); }
}

public sealed class ServerViewModel(ServerConfig config) : ObservableObject
{
    public ServerConfig Config { get; private set; } = config;
    public ServerReading Reading { get; private set; } = new(config);
    public string Name => Config.Name;
    public string Target => Config.Target;
    public string Status => !Config.Enabled ? Localizer.Current["Disabled"] : Localizer.Current.Activity(Reading.Activity);
    public Brush StatusBrush => (Brush)System.Windows.Application.Current.FindResource(!Config.Enabled ? "MutedBrush" : Reading.Activity switch
    { ServerActivity.Busy => "BusyBrush", ServerActivity.Idle or ServerActivity.CpuOnly => "GreenBrush",
        ServerActivity.Offline => "DangerBrush", _ => "MutedBrush" });
    private ServerSnapshot? Snapshot => Reading.Snapshot;
    public string Summary => Snapshot is null ? "—" : Snapshot.IsGpuServer
        ? $"{Snapshot.Gpus.Length} GPU · {DisplayFormat.Mib(Snapshot.Gpus.Sum(g => g.MemoryUsedMiB))}"
        : $"CPU {Cpu} · RAM {Ram}";
    public string Subtitle => Snapshot is null ? Target + " · " + Status
        : $"{Snapshot.System.Hostname} · {Snapshot.System.Os} · {Localizer.Current["Uptime"]} {DisplayFormat.Duration(Snapshot.System.UptimeSeconds)}";
    public string Cpu => DisplayFormat.Percent(Snapshot?.System.CpuUsage);
    public string Cores => Snapshot?.System.CpuCores is { } cores ? $"{cores} {Localizer.Current["Cores"]}" : "—";
    public string Ram => DisplayFormat.Percent(Snapshot?.System.MemFraction);
    public string RamSize => DisplayFormat.Mib(Snapshot?.System.MemTotalKB / 1024);
    public string Disk => DisplayFormat.Percent(Snapshot?.System.DiskFraction);
    public string DiskSize => DisplayFormat.Mib(Snapshot?.System.DiskTotalKB / 1024);
    public string Rx => "↓ " + DisplayFormat.Rate(Snapshot?.System.NetRxRate);
    public string Tx => "↑ " + DisplayFormat.Rate(Snapshot?.System.NetTxRate);
    public string? Error => Reading.Error;
    public string ErrorTitle => Localizer.Current[SshDiagnostics.Classify(Error) switch
    {
        SshFailureKind.UnverifiedHost => "UnverifiedHost", SshFailureKind.ChangedHost => "ChangedHost",
        SshFailureKind.MissingKey => "MissingKey", SshFailureKind.KeyPermissions => "KeyPermissions",
        SshFailureKind.Authentication => "Authentication", SshFailureKind.Timeout => "ConnectionTimeout", _ => "Error"
    }];
    public string ErrorHint => Localizer.Current[SshDiagnostics.Classify(Error) switch
    {
        SshFailureKind.UnverifiedHost => "UnverifiedHostHint", SshFailureKind.ChangedHost => "ChangedHostHint",
        SshFailureKind.MissingKey => "MissingKeyHint", SshFailureKind.KeyPermissions => "KeyPermissionsHint",
        SshFailureKind.Authentication => "AuthenticationHint", _ => "RetryHint"
    }];
    public Visibility ErrorVisibility => string.IsNullOrEmpty(Error) ? Visibility.Collapsed : Visibility.Visible;
    public string GpuMessage => Snapshot is null ? Localizer.Current["NoData"] : Snapshot.GpuError
        ?? (Snapshot.IsGpuServer ? "" : Localizer.Current["NoGpu"]);
    public Visibility GpuVisibility => Snapshot?.IsGpuServer == true ? Visibility.Visible : Visibility.Collapsed;
    public Visibility GpuMessageVisibility => string.IsNullOrEmpty(GpuMessage) ? Visibility.Collapsed : Visibility.Visible;
    public GpuViewModel[] Gpus => Snapshot?.Gpus.Select(g => new GpuViewModel(g)).ToArray() ?? [];
    public ProcessViewModel[] Processes => Snapshot?.Tasks.Concat(Snapshot.Processes).DistinctBy(p => p.Pid)
        .Select(p => new ProcessViewModel(p)).ToArray() ?? [];
    public JobViewModel[] Jobs => Reading.Jobs?.Select(j => new JobViewModel(j)).ToArray() ?? [];
    public string JobMessage => Jobs.Length == 0 ? Localizer.Current["NoJobs"] : "";
    public void Apply(ServerReading reading) { Reading = reading; AllChanged(); }
    public void SetConfig(ServerConfig config) { Config = config; AllChanged(); }
    public void Refresh() => AllChanged();
}

public sealed record GpuViewModel(GpuStat Stat)
{
    public string Name => $"GPU {Stat.Index}  ·  {Stat.Name}";
    public string Telemetry => $"{(Stat.Temperature is { } t ? $"{t:0} °C" : "—")}  ·  {(Stat.PowerDraw is { } p ? $"{p:0} W" : "—")}";
    public string Utilization => $"{Stat.Utilization:0}%";
    public double UtilizationValue => Math.Clamp(Stat.Utilization, 0, 100);
    public string Memory => $"{DisplayFormat.Mib(Stat.MemoryUsedMiB)} / {DisplayFormat.Mib(Stat.MemoryTotalMiB)}";
    public double MemoryValue => Stat.MemoryFraction * 100;
}
public sealed record ProcessViewModel(RemoteTask Task)
{
    public int Pid => Task.Pid;
    public string User => Task.User;
    public string Cpu => Task.CpuPercent is { } n ? $"{n:0}%" : "—";
    public string Ram => DisplayFormat.Mib(Task.RssKB / 1024);
    public string Command => Task.Command;
    public string Detail => $"{Task.Command}\n{Localizer.Current["Cwd"]}: {Task.Cwd ?? "—"}\n{Localizer.Current["Elapsed"]}: {DisplayFormat.Duration(Task.ElapsedSeconds)}\nGPU: {string.Join(", ", Task.GpuIndices)}";
}
public sealed record JobViewModel(TaskRecord Record)
{
    public string Command => Record.Command;
    public string Detail => $"{Record.User} · GPU {string.Join(",", Record.GpuIndices)} · {Record.StartedAt.LocalDateTime:g} → "
        + (Record.EndedAt is { } ended ? ended.LocalDateTime.ToString("g") : Localizer.Current["Running"]);
}
