namespace WctControls;

internal sealed class CardsPage : Component
{
    public override Element Render()
    {
        var (wifiOn, setWifiOn) = UseState(true);
        var (clicks, setClicks) = UseState(0);

        return Gallery.Page(
            "SettingsCard",
            "A settings row: header + description, content on the right, and an optional whole-card click.",
            VStack(12,
                SettingsCard(
                    header: "Wi-Fi",
                    description: wifiOn ? "Connected to CONTOSO-5G" : "Disconnected",
                    content: ToggleSwitch(isOn: wifiOn, onIsOnChanged: setWifiOn),
                    headerIcon: Icon(FontIcon("\uE701"))),
                SettingsCard(
                    header: "About",
                    description: $"Tapped {clicks} time(s) — click anywhere on this card",
                    isClickEnabled: true,
                    onClick: () => setClicks(clicks + 1))));
    }
}
