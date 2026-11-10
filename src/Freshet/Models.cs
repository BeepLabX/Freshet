using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace Freshet;

public class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public sealed class PackageEntry : Observable
{
    [JsonIgnore]
    public int ListOrder { get; set; }
    public string Name { get; set; } = "";
    public string Id { get; set; } = "";
    public string Installed { get; set; } = "";
    public string Available { get; set; } = "";
    public string Source { get; set; } = "";
    private ImageSource icon = AppIcons.Default;
    [JsonIgnore]
    public ImageSource Icon { get => icon; set { icon = value; Changed(); Changed(nameof(IconBackground)); } }
    [JsonIgnore]
    public Brush IconBackground => AppIcons.BackgroundFor(icon);
    private string iconOrigin = "기본 아이콘";
    public string IconOrigin { get => iconOrigin; set { iconOrigin = value; Changed(); } }
    private long? estimatedBytes;
    public long? EstimatedBytes { get => estimatedBytes; set { estimatedBytes = value; Changed(); Changed(nameof(SizeText)); } }
    private string sizeState = "pending";
    public string SizeState { get => sizeState; set { sizeState = value; Changed(); Changed(nameof(SizeText)); } }
    private string sizeNote = "설치 파일의 예상 용량을 조회합니다. 추가 다운로드와 의존 패키지는 포함되지 않을 수 있습니다.";
    public string SizeNote { get => sizeNote; set { sizeNote = value; Changed(); } }
    public string SizeText => EstimatedBytes is long bytes ? "약 " + DownloadSizes.Format(bytes) : SizeState == "pending" ? "조회 중…" : "확인 불가";
    private bool selected;
    public bool Selected { get => selected; set { selected = value; Changed(); } }
    private bool hidden;
    [JsonIgnore]
    public bool Hidden { get => hidden; set { hidden = value; if (value) Selected = false; Changed(); Changed(nameof(CanSelect)); } }
    private bool excluded;
    public bool Excluded { get => excluded; set { excluded = value; if (value) Selected = false; Changed(); Changed(nameof(CanSelect)); Changed(nameof(HoldText)); } }
    private bool locked;
    public bool Locked { get => locked; set { locked = value; Changed(); Changed(nameof(CanSelect)); Changed(nameof(CanHold)); } }
    public bool CanSelect => !Excluded && !Hidden && !Locked;
    public bool CanHold => !Locked;
    public string HoldText => Excluded ? "보류됨 ▾" : "보류 ▾";
    private string status = "업데이트 가능", tone = "Success";
    public string Status => status;
    [JsonIgnore]
    public Brush StatusColor => Theme.Brush(tone);
    public void SetStatus(string text, string statusTone = "Muted") { status = text; tone = statusTone; Changed(nameof(Status)); Changed(nameof(StatusColor)); }
    public void SetOutcome(HistoryEntry h) => SetStatus(h.OutcomeText, h.Tone);
    public void ThemeChanged() => Changed(nameof(StatusColor));
}

public sealed class HistoryEntry
{
    public string RecordId { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset Started { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? Finished { get; set; }
    public string Computer { get; set; } = Environment.MachineName;
    public string Name { get; set; } = "";
    public string Id { get; set; } = "";
    public string Source { get; set; } = "";
    public string Before { get; set; } = "";
    public string Target { get; set; } = "";
    public string After { get; set; } = "";
    public string Outcome { get; set; } = "running";
    public int? ExitCode { get; set; }
    public string LogFile { get; set; } = "";
    public string Detail { get; set; } = "";
    public string When => Started.ToLocalTime().ToString("yyyy.MM.dd  HH:mm");
    public string VersionChange => $"{Before}  →  {(string.IsNullOrWhiteSpace(After) ? Target + " (목표)" : After)}";
    public string OutcomeText => Outcome switch {
        "success" => "완료 · 버전 확인", "unverified" => "적용 확인 안 됨", "app-restart" => "앱 재시작 필요",
        "reboot" => "다시 시작 필요", "failed" => "실패", "interrupted" => "중단 · 확인 필요", _ => "실행 중"
    };
    [System.Text.Json.Serialization.JsonIgnore]
    public string Tone => Outcome switch { "success" => "Success", "failed" => "Danger", "running" => "AccentText", _ => "Warning" };
}

public sealed class Preferences
{
    public bool ReduceMotion { get; set; }
    public bool SilentInstall { get; set; } = true;
    public bool ScanOnLaunch { get; set; } = true;
    public string LastScan { get; set; } = "아직 조회하지 않았습니다";
    public List<string> ExcludedIds { get; set; } = [];
    public List<HiddenPackage> HiddenPackages { get; set; } = [];
}

public sealed record HiddenPackage(string Name, string Id, string Source)
{
    public bool Matches(PackageEntry p) => Id.Equals(p.Id, StringComparison.OrdinalIgnoreCase) && Source.Equals(p.Source, StringComparison.OrdinalIgnoreCase);
}
