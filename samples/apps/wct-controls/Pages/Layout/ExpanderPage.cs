namespace WctControls;

internal sealed class ExpanderPage : Component
{
    public override Element Render()
    {
        var (notifications, setNotifications) = UseState(true);
        var (sounds, setSounds) = UseState(false);

        return Gallery.Page(
            "SettingsExpander",
            "A settings group that expands to reveal child cards (populated through the generated items slot).",
            SettingsExpander(
                header: "Notifications",
                description: notifications ? "On" : "Off",
                isExpanded: true,
                items: new object[]
                {
                    SettingsCard(
                        header: "Show notifications",
                        content: ToggleSwitch(isOn: notifications, onIsOnChanged: setNotifications)),
                    SettingsCard(
                        header: "Play sounds",
                        content: ToggleSwitch(isOn: sounds, onIsOnChanged: setSounds)),
                }));
    }
}
