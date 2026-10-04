using Forms = System.Windows.Forms;

namespace Swish.App;

/// <summary>System-tray icon and menu. (WPF has no tray API, so this borrows WinForms' NotifyIcon.)</summary>
sealed class TrayIcon : IDisposable
{
    readonly Forms.NotifyIcon _icon;
    readonly Forms.ToolStripMenuItem _handMouseItem;
    readonly Forms.ToolStripMenuItem _voiceItem;
    readonly Forms.ToolStripMenuItem _presetMenu;
    readonly Action<string> _choosePreset;

    public TrayIcon(Action showWindow, Action toggleHandMouse, Action toggleVoice, Action<string> choosePreset,
                    Action recalibrate, Action openPresetsFolder, Action exit)
    {
        _choosePreset = choosePreset;
        _handMouseItem = new Forms.ToolStripMenuItem("Hand mouse  (Ctrl+Alt+M)", null, (_, _) => toggleHandMouse());
        _voiceItem = new Forms.ToolStripMenuItem("Voice commands", null, (_, _) => toggleVoice());
        _presetMenu = new Forms.ToolStripMenuItem("Preset");

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Show SWISH", null, (_, _) => showWindow());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(_handMouseItem);
        menu.Items.Add(_voiceItem);
        menu.Items.Add(_presetMenu);
        menu.Items.Add("Recalibrate hand…", null, (_, _) => recalibrate());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Open presets folder", null, (_, _) => openPresetsFolder());
        menu.Items.Add("Exit", null, (_, _) => exit());

        _icon = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "SWISH",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => showWindow();
    }

    public void SetPresets(IEnumerable<(string Id, string Name)> presets)
    {
        _presetMenu.DropDownItems.Clear();
        foreach (var (id, name) in presets)
            _presetMenu.DropDownItems.Add(new Forms.ToolStripMenuItem(name, null, (_, _) => _choosePreset(id)) { Tag = id });
    }

    public void SetState(bool handMouse, bool? voiceListening, string? presetId)
    {
        _handMouseItem.Checked = handMouse;
        _voiceItem.Enabled = voiceListening is not null;
        _voiceItem.Checked = voiceListening == true;
        foreach (Forms.ToolStripMenuItem item in _presetMenu.DropDownItems)
            item.Checked = (string)item.Tag! == presetId;
        _icon.Text = $"SWISH: hand {(handMouse ? "on" : "off")}, voice {(voiceListening switch { true => "on", false => "paused", null => "off" })}";
    }

    public void Notify(string title, string text) =>
        _icon.ShowBalloonTip(1500, title, text, Forms.ToolTipIcon.None);

    public void Dispose()
    {
        _icon.Visible = false; // otherwise a ghost icon lingers until you hover it
        _icon.Dispose();
    }
}
