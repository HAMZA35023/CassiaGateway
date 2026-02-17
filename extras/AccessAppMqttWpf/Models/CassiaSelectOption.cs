namespace AccessAppMqttWpf.Models;

public sealed class CassiaSelectOption
{
    public CassiaSelectOption(string value, string label)
    {
        Value = value ?? "";
        Label = string.IsNullOrWhiteSpace(label) ? Value : label.Trim();
    }

    public string Value { get; }
    public string Label { get; }
}
