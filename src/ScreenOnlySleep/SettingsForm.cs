using System;
using System.Drawing;
using System.Windows.Forms;

namespace ScreenOnlySleep
{
  internal sealed class SettingsForm : Form
  {
    readonly HotkeyBox hotkeyBox;
    readonly CheckBox autostartBox, ddcBox;
    readonly NumericUpDown hoursBox;
    readonly Func<Hotkey, bool> tryRegister;
    readonly bool autostartInitial;

    public Settings Result { get; private set; }
    public bool Autostart { get { return autostartBox.Checked; } }
    public bool AutostartChanged { get { return autostartBox.Checked != autostartInitial; } }

    // tryRegister registers the chosen hotkey and returns false if another app already uses it.
    public SettingsForm(Settings current, bool autostartOn, Func<Hotkey, bool> tryRegister)
    {
      this.tryRegister = tryRegister;
      autostartInitial = autostartOn;
      Result = current.Clone();

      Text = Strings.SettingsTitle;
      FormBorderStyle = FormBorderStyle.FixedDialog;
      MaximizeBox = MinimizeBox = false;
      ShowInTaskbar = true;
      StartPosition = FormStartPosition.CenterScreen;
      AutoScaleMode = AutoScaleMode.Dpi;
      AutoSize = true;
      AutoSizeMode = AutoSizeMode.GrowAndShrink;
      Padding = new Padding(12);
      Font = SystemFonts.MessageBoxFont;

      var layout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };

      layout.Controls.Add(new Label { Text = Strings.LabelHotkey, AutoSize = true, Margin = new Padding(3, 3, 3, 0) });
      hotkeyBox = new HotkeyBox(current.Hotkey) { Width = 260 };
      layout.Controls.Add(hotkeyBox);

      autostartBox = new CheckBox { Text = Strings.CheckAutostart, Checked = autostartOn, AutoSize = true, Margin = new Padding(3, 12, 3, 3) };
      layout.Controls.Add(autostartBox);

      ddcBox = new CheckBox { Text = Strings.CheckDdc, Checked = current.UseDdc, AutoSize = true };
      layout.Controls.Add(ddcBox);

      layout.Controls.Add(new Label { Text = Strings.LabelHours, AutoSize = true, Margin = new Padding(3, 12, 3, 0) });
      hoursBox = new NumericUpDown
      {
        DecimalPlaces = 2,
        Increment = 0.5m,
        Minimum = (decimal)Settings.MinAutoReleaseHours,
        Maximum = (decimal)Settings.MaxAutoReleaseHours,
        Value = (decimal)Settings.ClampHours(current.AutoReleaseHours),
        Width = 100,
      };
      layout.Controls.Add(hoursBox);

      var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 16, 0, 0) };
      var cancel = new Button { Text = Strings.ButtonCancel, DialogResult = DialogResult.Cancel, AutoSize = true };
      var ok = new Button { Text = Strings.ButtonOk, AutoSize = true };
      ok.Click += OnOk;
      buttons.Controls.Add(cancel);
      buttons.Controls.Add(ok);
      layout.Controls.Add(buttons);

      Controls.Add(layout);
      AcceptButton = ok;
      CancelButton = cancel;
    }

    void OnOk(object sender, EventArgs e)
    {
      var h = hotkeyBox.Hotkey;
      if (h == null || !h.IsValid)
      {
        MessageBox.Show(this, Strings.InvalidHotkey, Strings.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        hotkeyBox.Focus();
        return;
      }
      if (!tryRegister(h))
      {
        MessageBox.Show(this, Strings.HotkeyTaken(h.ToString()), Strings.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        hotkeyBox.Focus();
        return;
      }
      Result = new Settings { Hotkey = h, UseDdc = ddcBox.Checked, AutoReleaseHours = Settings.ClampHours((double)hoursBox.Value) };
      DialogResult = DialogResult.OK;
      Close();
    }
  }

  // Text box that records a key combination instead of text.
  internal sealed class HotkeyBox : TextBox
  {
    public Hotkey Hotkey { get; private set; }

    public HotkeyBox(Hotkey initial)
    {
      Hotkey = initial;
      ShortcutsEnabled = false;
      Text = initial.ToString();
    }

    protected override bool IsInputKey(Keys keyData)
    {
      // plain Tab / Shift+Tab still move the focus; plain Enter and Esc still press OK and Cancel
      var code = keyData & Keys.KeyCode;
      bool noCtrlAlt = (keyData & (Keys.Control | Keys.Alt)) == 0;
      if (code == Keys.Tab && noCtrlAlt) return false;
      if ((code == Keys.Enter || code == Keys.Escape) && (keyData & Keys.Modifiers) == 0) return false;
      return true;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
      // let Alt combinations reach OnKeyDown instead of the menu / mnemonic handling
      if ((keyData & Keys.Alt) != 0) { OnKeyDown(new KeyEventArgs(keyData)); return true; }
      return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
      e.Handled = true;
      e.SuppressKeyPress = true;
      var mods = HotkeyModifiers.None;
      if (e.Control) mods |= HotkeyModifiers.Ctrl;
      if (e.Alt) mods |= HotkeyModifiers.Alt;
      if (e.Shift) mods |= HotkeyModifiers.Shift;
      if (NativeMethods.IsKeyDown(Hotkey.VK_LWIN) || NativeMethods.IsKeyDown(Hotkey.VK_RWIN)) mods |= HotkeyModifiers.Win;
      var key = e.KeyCode;
      bool modifierOnly = key == Keys.ControlKey || key == Keys.ShiftKey || key == Keys.Menu || key == Keys.LWin || key == Keys.RWin;
      if (modifierOnly)
      {
        Text = new Hotkey(mods, Keys.None).ToString().Replace("None", "");
        return;
      }
      var h = new Hotkey(mods, key);
      Hotkey = h.IsValid ? h : null;
      Text = h.ToString();
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
      e.Handled = true;
      // a half-typed combination goes back to the last valid one
      if (Hotkey != null) Text = Hotkey.ToString();
    }

    protected override void OnKeyPress(KeyPressEventArgs e) { e.Handled = true; }
  }
}
