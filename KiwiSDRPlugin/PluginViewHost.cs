using ENSdr.Plugins;

namespace KiwiSDRPlugin.Views;

internal static class PluginViewHost
{
    public static Control KeepOrCreate(ref Control? slot, Func<Control> create)
    {
        if (slot is { IsDisposed: false }) return slot;
        return slot = create();
    }

    public static void Bind(Control view, IAfPluginUiHost ui, Func<IReadOnlyDictionary<string, string>> snapshot,
        Action<IReadOnlyDictionary<string, string>>? load = null)
    {
        load?.Invoke(ui.LoadOptions());
        void Push(string? command = null)
        {
            var options = new Dictionary<string, string>(snapshot(), StringComparer.OrdinalIgnoreCase)
            {
                ["enabled"] = ui.IsRunning.ToString()
            };
            if (!string.IsNullOrEmpty(command)) options["command"] = command;
            ui.SaveOptions(options);
            ui.Configure(options);
        }
        if (view is IPluginViewEvents events)
        {
            events.OptionsChanged += () => Push();
            events.CommandRequested += command => Push(command);
        }
        Push();
    }
}

internal interface IPluginViewEvents
{
    event Action? OptionsChanged;
    event Action<string>? CommandRequested;
}
