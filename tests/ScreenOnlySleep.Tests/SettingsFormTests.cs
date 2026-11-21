using System.Reflection;
using System.Windows.Forms;
using Xunit;

namespace ScreenOnlySleep.Tests
{
  public class SettingsFormTests
  {
    static void Invoke(object target, string method, params object[] args)
    {
      target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, args);
    }

    [Fact]
    public void HotkeyBoxRecordsCombinations()
    {
      using (var box = new HotkeyBox(Hotkey.Default))
      {
        Assert.Equal("Ctrl+Alt+F12", box.Text);
        Invoke(box, "OnKeyDown", new KeyEventArgs(Keys.Control | Keys.Shift | Keys.K));
        Assert.Equal("Ctrl+Shift+K", box.Hotkey.ToString());
        Assert.Equal("Ctrl+Shift+K", box.Text);
        Invoke(box, "OnKeyDown", new KeyEventArgs(Keys.Control | Keys.ControlKey));   // modifier alone: no change
        Assert.Equal("Ctrl+Shift+K", box.Hotkey.ToString());
        Invoke(box, "OnKeyDown", new KeyEventArgs(Keys.F5));   // no modifier: invalid
        Assert.Null(box.Hotkey);
      }
    }

    [Theory]
    [InlineData(Keys.Enter, false)]
    [InlineData(Keys.Escape, false)]
    [InlineData(Keys.Tab, false)]
    [InlineData(Keys.Tab | Keys.Shift, false)]
    [InlineData(Keys.Enter | Keys.Control, true)]
    [InlineData(Keys.Escape | Keys.Shift, true)]
    [InlineData(Keys.Tab | Keys.Control, true)]
    [InlineData(Keys.F12, true)]
    public void DialogKeysStillWork(Keys key, bool captured)
    {
      using (var box = new HotkeyBox(Hotkey.Default))
      {
        var m = typeof(HotkeyBox).GetMethod("IsInputKey", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.Equal(captured, (bool)m.Invoke(box, new object[] { key }));
      }
    }

    [Fact]
    public void LayoutFitsTheWindow()
    {
      using (var form = new SettingsForm(new Settings(), false, h => true))
      {
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new System.Drawing.Point(-3000, -3000);
        form.ShowInTaskbar = false;
        form.Show();
        Application.DoEvents();
        var client = form.ClientRectangle;
        foreach (Control c in AllControls(form))
        {
          var r = form.RectangleToClient(c.RectangleToScreen(c.ClientRectangle));
          Assert.True(client.Contains(r), c.GetType().Name + " '" + c.Text + "' " + r + " outside " + client);
        }
        form.Close();
      }
    }

    static System.Collections.Generic.IEnumerable<Control> AllControls(Control root)
    {
      foreach (Control c in root.Controls)
      {
        yield return c;
        foreach (var d in AllControls(c)) yield return d;
      }
    }

    [Fact]
    public void OkReturnsTheNewSettingsAfterRegistering()
    {
      Hotkey registered = null;
      var current = new Settings();
      using (var form = new SettingsForm(current, false, h => { registered = h; return true; }))
      {
        var box = (HotkeyBox)typeof(SettingsForm).GetField("hotkeyBox", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
        var ddc = (CheckBox)typeof(SettingsForm).GetField("ddcBox", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
        var hours = (NumericUpDown)typeof(SettingsForm).GetField("hoursBox", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
        var auto = (CheckBox)typeof(SettingsForm).GetField("autostartBox", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
        Invoke(box, "OnKeyDown", new KeyEventArgs(Keys.Control | Keys.Alt | Keys.F11));
        ddc.Checked = false;
        hours.Value = 2.5m;
        auto.Checked = true;
        Invoke(form, "OnOk", null, System.EventArgs.Empty);
        Assert.Equal(DialogResult.OK, form.DialogResult);
        Assert.Equal("Ctrl+Alt+F11", registered.ToString());
        Assert.Equal("Ctrl+Alt+F11", form.Result.Hotkey.ToString());
        Assert.False(form.Result.UseDdc);
        Assert.Equal(2.5, form.Result.AutoReleaseHours);
        Assert.True(form.Autostart);
        Assert.True(form.AutostartChanged);
        Assert.True(current.UseDdc);   // the original object is untouched
      }
    }
  }
}
