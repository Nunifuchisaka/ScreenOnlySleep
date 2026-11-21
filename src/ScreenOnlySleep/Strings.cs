using System.Globalization;

namespace ScreenOnlySleep
{
  // UI text. Japanese when the Windows display language is Japanese, English otherwise.
  internal static class Strings
  {
    static readonly bool Ja = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ja";

    public static string AppName { get { return "ScreenOnlySleep"; } }
    public static string MenuBlankNow { get { return Ja ? "今すぐ画面オフ" : "Turn screens off now"; } }
    public static string MenuSettings { get { return Ja ? "設定" : "Settings"; } }
    public static string MenuExit { get { return Ja ? "終了" : "Exit"; } }
    public static string HotkeyNotRegisteredSuffix { get { return Ja ? "（ホットキー未登録）" : " (hotkey not registered)"; } }

    public static string TrayTip(string hotkey) { return Ja ? "ScreenOnlySleep（" + hotkey + "）" : "ScreenOnlySleep (" + hotkey + ")"; }

    public static string HotkeyConflictTitle { get { return Ja ? "ホットキーを登録できません" : "Hotkey not available"; } }
    public static string HotkeyConflict(string hotkey)
    {
      return Ja
        ? hotkey + " は別のアプリが使っているため登録できませんでした。トレイのメニューから画面オフにするか、設定で別のキーを選んでください。"
        : hotkey + " is already used by another app. Use the tray menu, or choose another hotkey in Settings.";
    }

    public static string AlreadyRunning { get { return Ja ? "ScreenOnlySleep はすでに起動しています。タスクトレイのアイコンから操作してください。" : "ScreenOnlySleep is already running. Use its icon in the notification area."; } }

    public static string SettingsTitle { get { return Ja ? "ScreenOnlySleep の設定" : "ScreenOnlySleep Settings"; } }
    public static string LabelHotkey { get { return Ja ? "ホットキー（入力欄でキーを押す）" : "Hotkey (press the keys in the box)"; } }
    public static string CheckAutostart { get { return Ja ? "Windows の起動時に自動で起動する" : "Start with Windows"; } }
    public static string CheckDdc { get { return Ja ? "対応モニターは輝度も 0 にする（DDC/CI）" : "Also set brightness to 0 on supported monitors (DDC/CI)"; } }
    public static string LabelHours { get { return Ja ? "自動解除までの時間（時間）" : "Turn screens back on after (hours)"; } }
    public static string ButtonOk { get { return "OK"; } }
    public static string ButtonCancel { get { return Ja ? "キャンセル" : "Cancel"; } }
    public static string InvalidHotkey { get { return Ja ? "Ctrl・Alt・Shift・Win のどれかと、ほかのキーを組み合わせてください（F13〜F24 は使えません）。" : "Combine Ctrl, Alt, Shift or Win with another key (F13-F24 cannot be used)."; } }
    public static string HotkeyTaken(string hotkey) { return Ja ? hotkey + " は別のアプリが使っています。別の組み合わせを選んでください。" : hotkey + " is already used by another app. Choose another combination."; }
    public static string AutostartFailed(string message) { return Ja ? "自動起動の設定を変更できませんでした: " + message : "Could not change the start-with-Windows setting: " + message; }
  }
}
