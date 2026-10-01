namespace NeuroSDR.Controls;

/// <summary>
/// Title-bar icon menu. Add entries with <see cref="AddCommand"/> as features grow.
/// </summary>
internal sealed class AppChromeMenu : ContextMenuStrip
{
    private readonly Form _owner;
    private readonly List<(string Text, Action<Form> Action)> _extra = [];
    private Action? _exportSettings;
    private Action? _importSettings;
    private Action? _resetSettings;

    public AppChromeMenu(Form owner)
    {
        _owner = owner;
        ShowImageMargin = false;
        BackColor = Color.FromArgb(18, 36, 48);
        ForeColor = Color.FromArgb(230, 238, 245);
        Font = new Font("Segoe UI", 9f);
        Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors());
        Rebuild();
    }

    public void AddCommand(string text, Action<Form> action)
    {
        _extra.Add((text, action));
        Rebuild();
    }

    public void Rebuild()
    {
        Items.Clear();
        if (_exportSettings is not null && _importSettings is not null && _resetSettings is not null)
        {
            var settings = new ToolStripMenuItem("Config");
            var exportAction = _exportSettings;
            var importAction = _importSettings;
            var resetAction = _resetSettings;
            var export = new ToolStripMenuItem("Export...");
            export.Click += (_, _) => exportAction();
            var import = new ToolStripMenuItem("Import...");
            import.Click += (_, _) => importAction();
            var reset = new ToolStripMenuItem("Reset...");
            reset.Click += (_, _) => resetAction();
            settings.DropDownItems.Add(export);
            settings.DropDownItems.Add(import);
            settings.DropDownItems.Add(new ToolStripSeparator());
            settings.DropDownItems.Add(reset);
            settings.DropDown.Renderer = Renderer;
            settings.DropDown.BackColor = BackColor;
            settings.DropDown.ForeColor = ForeColor;
            Items.Add(settings);
            Items.Add(new ToolStripSeparator());
        }

        var about = new ToolStripMenuItem($"&About NeuroSDR {AppIcons.ProductVersion}");
        about.Click += (_, _) => AboutNeuroSdrForm.ShowFor(_owner);
        Items.Add(about);
        if (_extra.Count > 0)
        {
            Items.Add(new ToolStripSeparator());
            foreach (var (text, action) in _extra)
            {
                var item = new ToolStripMenuItem(text);
                var captured = action;
                item.Click += (_, _) => captured(_owner);
                Items.Add(item);
            }
        }
    }

    public void AttachSettingsMenu(Action export, Action import, Action reset)
    {
        _exportSettings = export;
        _importSettings = import;
        _resetSettings = reset;
        Rebuild();
    }

    private sealed class DarkMenuColors : ProfessionalColorTable
    {
        public override Color MenuItemSelected => Color.FromArgb(45, 80, 100);
        public override Color MenuItemBorder => Color.FromArgb(86, 130, 151);
        public override Color MenuBorder => Color.FromArgb(70, 110, 130);
        public override Color ToolStripDropDownBackground => Color.FromArgb(18, 36, 48);
        public override Color ImageMarginGradientBegin => Color.FromArgb(18, 36, 48);
        public override Color ImageMarginGradientMiddle => Color.FromArgb(18, 36, 48);
        public override Color ImageMarginGradientEnd => Color.FromArgb(18, 36, 48);
        public override Color SeparatorDark => Color.FromArgb(60, 90, 110);
        public override Color SeparatorLight => Color.FromArgb(40, 65, 80);
    }
}
