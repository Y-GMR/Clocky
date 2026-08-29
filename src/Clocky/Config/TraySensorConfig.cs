namespace Clocky.Config;

public class TraySensorConfig
{
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string SensorType { get; set; } = string.Empty; // "Temperature", "Power", "Utilization", "BatteryTime", "BatteryPercent", etc.
    public bool Enabled { get; set; } = true;
    public string BackgroundColorHex { get; set; } = "#16A34A";
    public string TextColorHex { get; set; } = "#FFFFFF";
    public string Unit { get; set; } = string.Empty;
    public int Order { get; set; } = 0;
}
