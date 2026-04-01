namespace AccessAppMqttWpf.Models;

/// <summary>WPF-side representation of a decoded DALI bus frame, received via MQTT.</summary>
public sealed class DaliLogEntry
{
    public int?           Seq             { get; set; }
    public DateTimeOffset Timestamp       { get; set; }
    public string         RawHex          { get; set; } = "";
    public string         SignalType      { get; set; } = "";
    public string?        AddressType     { get; set; }
    public int?           Address         { get; set; }
    public string?        InstanceType    { get; set; }
    public string         Description     { get; set; } = "";
    public string?        ValueHex        { get; set; }
    public int?           ValueDec        { get; set; }
    public string         FullTranslation { get; set; } = "";
    public int?           MissingEntries  { get; set; }
}
