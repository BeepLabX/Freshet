using System.Text.RegularExpressions;

namespace Freshet;

public static class UpdateSignals
{
    public static bool RequiresAppRestart(string text) => Regex.IsMatch(text,
        @"업그레이드를 완료하려면 (?:애플리케이션|앱)을 다시 시작|restart (?:the |this )?application to complete|restart (?:the |this )?app to complete",
        RegexOptions.IgnoreCase);
}

// winget writes carriage-return progress and can split any message between chunks.
// Keep a bounded tail and let the UI sample it, instead of dispatching every byte.
public sealed class UpdateProgress
{
    private readonly object gate = new();
    private string tail = "", phase = "준비 중", detail = "업데이트 정보를 확인하고 있습니다.";
    private int rank;
    public void Feed(string chunk)
    {
        lock (gate)
        {
            tail += chunk;
            if (tail.Length > 12000) tail = tail[^8000..];
            if (UpdateSignals.RequiresAppRestart(tail)) Set(4, "앱 재시작 필요", "앱을 완전히 종료한 뒤 다시 열어야 업데이트가 적용됩니다.");
            else if (Regex.IsMatch(tail, "패키지 설치를 시작|Starting package install|Successfully installed|설치 성공", RegexOptions.IgnoreCase))
                Set(3, "설치 중", "설치 프로그램이 작업 중입니다. 별도 설치 창이 열렸다면 안내를 확인하세요.");
            else if (Regex.IsMatch(tail, "해시를 확인|installer hash verified", RegexOptions.IgnoreCase))
                Set(2, "설치 파일 확인", "다운로드한 설치 파일의 무결성을 확인했습니다.");
            else
            {
                var matches = Regex.Matches(tail, @"(?<done>[\d.,]+)\s*(?<u1>[KMGT]?i?B)\s*/\s*(?<total>[\d.,]+)\s*(?<u2>[KMGT]?i?B)", RegexOptions.IgnoreCase);
                if (matches.Count > 0)
                {
                    var m = matches[^1];
                    Set(1, "다운로드 중", $"{DisplaySize(m.Groups["done"].Value, m.Groups["u1"].Value)} / {DisplaySize(m.Groups["total"].Value, m.Groups["u2"].Value)} · winget 전송 표시");
                }
                else if (Regex.IsMatch(tail, "다운로드 중|Downloading", RegexOptions.IgnoreCase)) Set(1, "다운로드 중", "설치 파일을 받고 있습니다. 전송량은 winget이 제공할 때 표시합니다.");
            }
        }
    }
    private static string DisplaySize(string value, string unit)
    {
        // Ambiguous locale separators are left out, never guessed into a total.
        if (!decimal.TryParse(value, System.Globalization.NumberStyles.AllowDecimalPoint,
            System.Globalization.CultureInfo.InvariantCulture, out var amount)) return "전송량 확인 중";
        int power = "KMGT".IndexOf(char.ToUpperInvariant(unit[0])) + 1;
        decimal factor = unit.Contains('i', StringComparison.OrdinalIgnoreCase) ? 1024 : 1000;
        for (int i = 0; i < power; i++) amount *= factor;
        return DownloadSizes.Format(amount);
    }
    private void Set(int next, string title, string message) { if (next >= rank) { rank = next; phase = title; detail = message; } }
    public void Verify() { lock (gate) Set(5, "설치 버전 확인", "목표 버전이 실제로 설치됐는지 확인하고 있습니다."); }
    public (string Phase, string Detail) Snapshot() { lock (gate) return (phase, detail); }
}
