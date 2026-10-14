using System.Globalization;
using OneFolderOneZip.Core;

namespace OneFolderOneZip.App;

internal static class Strings
{
    public static bool Japanese { get; } = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ja";
    public static string Pick(string ja, string en) => Japanese ? ja : en;
    public static string State(JobState state) => state switch
    {
        JobState.Scanning => Pick("スキャン中", "Scanning"), JobState.Waiting => Pick("待機中", "Waiting"),
        JobState.Compressing => Pick("圧縮中", "Compressing"), JobState.Completed => Pick("完了", "Done"),
        JobState.Error => Pick("エラー", "Error"), _ => Pick("キャンセル", "Cancelled")
    };
}
