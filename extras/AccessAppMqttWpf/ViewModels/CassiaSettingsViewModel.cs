using AccessAppMqttWpf.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;

namespace AccessAppMqttWpf.ViewModels;

public partial class CassiaSettingsViewModel : ObservableObject
{
    private const string DefaultEncryptedPassword = "U2FsdGVkX19Z8zcPoszwmXgWFuCY0jVVtUgDtisi/d0=";
    private static readonly bool ShowAllFields = true;

    private static readonly Regex Ipv4Regex = new(
        @"^(\d{1,2}|1\d\d|2[0-4]\d|25[0-5])(\.(\d{1,2}|1\d\d|2[0-4]\d|25[0-5])){3}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex NameRegex = new(
        @"^(?!_)(?!.*?_$)[a-zA-Z0-9_\/ \.\u4e00-\u9fa5]{0,30}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] TabOrder = { "status", "basic", "service", "container", "events", "other" };
    private static readonly Dictionary<string, string> TabTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["status"] = "Status",
        ["basic"] = "Basic",
        ["service"] = "Service",
        ["container"] = "Container",
        ["events"] = "Events",
        ["other"] = "Other"
    };

    private static readonly Dictionary<string, string[]> KnownAllowedValues = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fat"] = new[] { "0", "1", "2" },
        ["ble_power"] = new[] { "3", "8", "11", "15", "19" },
        ["stat_interval"] = new[] { "30", "60", "120", "300" },
        ["control_channel"] = new[] { "capwap", "mqtt" },
        ["ac.port"] = new[] { "5246,5247", "6246,6247" },
        ["ac.force_network"] = new[] { "1", "2", "3" },
        ["oauth-enable"] = new[] { "0", "1" },
        ["ssh-login"] = new[] { "0", "1" },
        ["wired.proto"] = new[] { "dhcp", "static" },
        ["wireless_ap.disabled"] = new[] { "0", "1" },
        ["wireless.mode"] = new[] { "sta", "ap", "disable" },
        ["wireless.encryption"] = new[] { "psk2", "psk-mixed+tkip+aes", "wpa2", "wpa-mixed+tkip+aes", "none" },
        ["wireless.eap"] = new[] { "peap-MSCHAPV2", "ttls-MSCHAPV2", "ttls-PAP", "tls" },
        ["wireless.proto"] = new[] { "dhcp", "static" },
        ["wireless.addnew"] = new[] { "no", "yes" },
        ["wireless1.encryption"] = new[] { "psk2", "psk-mixed+tkip+aes", "wpa2", "wpa-mixed+tkip+aes", "none" },
        ["wireless1.eap"] = new[] { "peap-MSCHAPV2", "ttls-MSCHAPV2", "ttls-PAP", "tls" },
        ["wireless1.proto"] = new[] { "dhcp" },
        ["wireless_verify"] = new[] { "off", "on" },
        ["adv_relay.filter_type"] = new[] { "none", "name", "addr", "raw" },
        ["adv_relay.filter_exclude"] = new[] { "0", "1" },
        ["dongle.defaultroute"] = new[] { "0", "1" },
        ["dongle.peerdns"] = new[] { "0", "1" },
        ["dongle.ipv6"] = new[] { "0", "1" },
        ["dongle.band_select"] = new[] { "0", "1" },
        ["dongle.dongle_recovery"] = new[] { "0", "1" },
        ["start.bypass.use"] = new[] { "sqs", "mqtt" },
        ["start.bypass.connection_type"] = new[] { "long", "short" },
        ["start.bypass.heartbeat_interval"] = new[] { "0", "30", "60", "180", "300", "600" },
        ["start.bypass.qos_of_scan"] = new[] { "0", "1", "2" },
        ["start.bypass.encryption_mode"] = new[] { "none", "psk", "certificate" },
        ["start.bypass.require_client_certificate"] = new[] { "no", "yes" },
        ["start.scanning.use"] = new[] { "", "passive", "active" },
        ["start.scanning.chip"] = new[] { "0", "1", "all" },
        ["start.scanning.timestamp"] = new[] { "0", "1", "2", "3" },
        ["start.scanning.data_type"] = new[] { "", "1", "2" },
        ["start.connection.use"] = new[] { "", "keep-alive", "period" },
        ["start.notification.use"] = new[] { "", "1" },
        ["start.notification.timestamp"] = new[] { "0", "1" },
        ["start.other.connection_list"] = new[] { "", "1", "2" },
        ["container.ssh-login"] = new[] { "1", "0" },
        ["proto1"] = new[] { "", "tcp", "udp" },
        ["proto2"] = new[] { "", "tcp", "udp" },
        ["proto3"] = new[] { "", "tcp", "udp" },
        ["proto4"] = new[] { "", "tcp", "udp" },
        ["local_btstack"] = new[] { "1", "0" },
        ["scan.filter_type"] = new[] { "raw", "mac", "" },
        ["scan.chip1.filter_type"] = new[] { "raw", "mac", "" },
        ["conn_params.type"] = new[] { "0", "2", "1" },
        ["conn_params.chip1.type"] = new[] { "0", "2", "1" },
        ["conn_params.data_length_ext"] = new[] { "251", "27" },
        ["conn_params.chip1.data_length_ext"] = new[] { "251", "27" },
        ["conn_params.connect_interval_priority"] = new[] { "0", "1" },
        ["conn_params.latency_priority"] = new[] { "0", "1" },
        ["conn_params.supervision_timeout_priority"] = new[] { "0", "1" },
        ["conn_params.chip1.connect_interval_priority"] = new[] { "0", "1" },
        ["conn_params.chip1.latency_priority"] = new[] { "0", "1" },
        ["conn_params.chip1.supervision_timeout_priority"] = new[] { "0", "1" },
        ["crtconfig"] = new[] { "default", "config" },
        ["log_level"] = new[] { "7", "6", "4", "2" },
        ["https"] = new[] { "0", "1" },
        ["tpm-enable"] = new[] { "0", "1" },
        ["bt_antenna_option"] = new[] { "0", "1", "2", "3" },
    };

    private static readonly Dictionary<string, Dictionary<string, string>> KnownOptionLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fat"] = new(StringComparer.OrdinalIgnoreCase) { ["0"] = "AC Managed Gateway", ["1"] = "Standalone Gateway", ["2"] = "Repeater Gateway" },
        ["stat_interval"] = new(StringComparer.OrdinalIgnoreCase) { ["30"] = "30 Seconds", ["60"] = "1 Minute", ["120"] = "2 Minutes", ["300"] = "5 Minutes" },
        ["control_channel"] = new(StringComparer.OrdinalIgnoreCase) { ["capwap"] = "CAPWAP", ["mqtt"] = "MQTT" },
        ["ac.force_network"] = new(StringComparer.OrdinalIgnoreCase) { ["1"] = "Wired", ["2"] = "Wi-Fi", ["3"] = "Cellular" },
        ["oauth-enable"] = new(StringComparer.OrdinalIgnoreCase) { ["0"] = "OFF", ["1"] = "ON" },
        ["ssh-login"] = new(StringComparer.OrdinalIgnoreCase) { ["0"] = "OFF", ["1"] = "ON" },
        ["wired.proto"] = new(StringComparer.OrdinalIgnoreCase) { ["dhcp"] = "DHCP", ["static"] = "Static" },
        ["wireless_ap.disabled"] = new(StringComparer.OrdinalIgnoreCase) { ["0"] = "Enable", ["1"] = "Disable" },
        ["wireless.mode"] = new(StringComparer.OrdinalIgnoreCase) { ["sta"] = "Client", ["ap"] = "Hotspot (Setup Only)", ["disable"] = "Disable" },
        ["wireless.encryption"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["psk2"] = "WPA2-PSK",
            ["psk-mixed+tkip+aes"] = "WPA[TKIP]+WPA2[AES]",
            ["wpa2"] = "[Enterprise]WPA2",
            ["wpa-mixed+tkip+aes"] = "[Enterprise]WPA[TKIP]+WPA2[AES]",
            ["none"] = "None"
        },
        ["wireless1.encryption"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["psk2"] = "WPA2-PSK",
            ["psk-mixed+tkip+aes"] = "WPA[TKIP]+WPA2[AES]",
            ["wpa2"] = "[Enterprise]WPA2",
            ["wpa-mixed+tkip+aes"] = "[Enterprise]WPA[TKIP]+WPA2[AES]",
            ["none"] = "None"
        },
        ["wireless.eap"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["peap-MSCHAPV2"] = "PEAP-MSCHAPV2",
            ["ttls-MSCHAPV2"] = "TTLS-MSCHAPV2",
            ["ttls-PAP"] = "TTLS-PAP",
            ["tls"] = "TLS"
        },
        ["wireless1.eap"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["peap-MSCHAPV2"] = "PEAP-MSCHAPV2",
            ["ttls-MSCHAPV2"] = "TTLS-MSCHAPV2",
            ["ttls-PAP"] = "TTLS-PAP",
            ["tls"] = "TLS"
        },
        ["wireless.proto"] = new(StringComparer.OrdinalIgnoreCase) { ["dhcp"] = "DHCP", ["static"] = "Static" },
        ["wireless1.proto"] = new(StringComparer.OrdinalIgnoreCase) { ["dhcp"] = "DHCP" },
        ["wireless.addnew"] = new(StringComparer.OrdinalIgnoreCase) { ["no"] = "No", ["yes"] = "Yes" },
        ["wireless_verify"] = new(StringComparer.OrdinalIgnoreCase) { ["off"] = "OFF", ["on"] = "ON" },
        ["adv_relay.filter_type"] = new(StringComparer.OrdinalIgnoreCase) { ["none"] = "None", ["name"] = "Name", ["addr"] = "Addr", ["raw"] = "Raw" },
        ["start.bypass.use"] = new(StringComparer.OrdinalIgnoreCase) { ["sqs"] = "SQS", ["mqtt"] = "MQTT" },
        ["start.bypass.connection_type"] = new(StringComparer.OrdinalIgnoreCase) { ["long"] = "Long", ["short"] = "Short" },
        ["start.bypass.heartbeat_interval"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["0"] = "OFF", ["30"] = "30 Seconds", ["60"] = "1 Minute", ["180"] = "3 Minutes", ["300"] = "5 Minutes", ["600"] = "10 Minutes"
        },
        ["start.bypass.qos_of_scan"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["0"] = "At Most Once (0)", ["1"] = "At Least Once (1)", ["2"] = "Exactly Once (2)"
        },
        ["start.bypass.encryption_mode"] = new(StringComparer.OrdinalIgnoreCase) { ["none"] = "None", ["psk"] = "PSK", ["certificate"] = "Certificate" },
        ["start.bypass.require_client_certificate"] = new(StringComparer.OrdinalIgnoreCase) { ["no"] = "No", ["yes"] = "Yes" },
        ["start.scanning.use"] = new(StringComparer.OrdinalIgnoreCase) { [""] = "OFF", ["passive"] = "Passive", ["active"] = "Active" },
        ["start.scanning.data_type"] = new(StringComparer.OrdinalIgnoreCase) { [""] = "Raw JSON", ["1"] = "MAC RSSI Only", ["2"] = "Beacon" },
        ["start.connection.use"] = new(StringComparer.OrdinalIgnoreCase) { [""] = "OFF", ["keep-alive"] = "Keep-Alive", ["period"] = "Period" },
        ["start.notification.use"] = new(StringComparer.OrdinalIgnoreCase) { [""] = "OFF", ["1"] = "ON" },
        ["start.other.connection_list"] = new(StringComparer.OrdinalIgnoreCase) { [""] = "OFF", ["1"] = "Connection Event Triggered", ["2"] = "Timer" },
        ["container.ssh-login"] = new(StringComparer.OrdinalIgnoreCase) { ["1"] = "ON", ["0"] = "OFF" },
        ["proto1"] = new(StringComparer.OrdinalIgnoreCase) { [""] = "N/A", ["tcp"] = "TCP", ["udp"] = "UDP" },
        ["proto2"] = new(StringComparer.OrdinalIgnoreCase) { [""] = "N/A", ["tcp"] = "TCP", ["udp"] = "UDP" },
        ["proto3"] = new(StringComparer.OrdinalIgnoreCase) { [""] = "N/A", ["tcp"] = "TCP", ["udp"] = "UDP" },
        ["proto4"] = new(StringComparer.OrdinalIgnoreCase) { [""] = "N/A", ["tcp"] = "TCP", ["udp"] = "UDP" },
        ["local_btstack"] = new(StringComparer.OrdinalIgnoreCase) { ["1"] = "Open", ["0"] = "Close" },
        ["conn_params.type"] = new(StringComparer.OrdinalIgnoreCase) { ["0"] = "Default", ["2"] = "High Speed Multiple Connection", ["1"] = "Custom" },
        ["conn_params.chip1.type"] = new(StringComparer.OrdinalIgnoreCase) { ["0"] = "Default", ["2"] = "High Speed Multiple Connection", ["1"] = "Custom" },
        ["scan.filter_type"] = new(StringComparer.OrdinalIgnoreCase) { ["raw"] = "Raw Data", ["mac"] = "MAC Address", [""] = "Null" },
        ["scan.chip1.filter_type"] = new(StringComparer.OrdinalIgnoreCase) { ["raw"] = "Raw Data", ["mac"] = "MAC Address", [""] = "Null" },
        ["conn_params.data_length_ext"] = new(StringComparer.OrdinalIgnoreCase) { ["251"] = "Enable", ["27"] = "Disable" },
        ["conn_params.chip1.data_length_ext"] = new(StringComparer.OrdinalIgnoreCase) { ["251"] = "Enable", ["27"] = "Disable" },
        ["conn_params.connect_interval_priority"] = new(StringComparer.OrdinalIgnoreCase) { ["0"] = "Device", ["1"] = "Gateway" },
        ["conn_params.latency_priority"] = new(StringComparer.OrdinalIgnoreCase) { ["0"] = "Device", ["1"] = "Gateway" },
        ["conn_params.supervision_timeout_priority"] = new(StringComparer.OrdinalIgnoreCase) { ["0"] = "Device", ["1"] = "Gateway" },
        ["conn_params.chip1.connect_interval_priority"] = new(StringComparer.OrdinalIgnoreCase) { ["0"] = "Device", ["1"] = "Gateway" },
        ["conn_params.chip1.latency_priority"] = new(StringComparer.OrdinalIgnoreCase) { ["0"] = "Device", ["1"] = "Gateway" },
        ["conn_params.chip1.supervision_timeout_priority"] = new(StringComparer.OrdinalIgnoreCase) { ["0"] = "Device", ["1"] = "Gateway" },
        ["crtconfig"] = new(StringComparer.OrdinalIgnoreCase) { ["default"] = "Default", ["config"] = "User Config" },
        ["log_level"] = new(StringComparer.OrdinalIgnoreCase) { ["7"] = "Info", ["6"] = "Minor", ["4"] = "Major", ["2"] = "Critical" },
        ["dongle.band_select"] = new(StringComparer.OrdinalIgnoreCase) { ["0"] = "Enable", ["1"] = "Disable" },
        ["dongle.dongle_recovery"] = new(StringComparer.OrdinalIgnoreCase) { ["0"] = "OFF", ["1"] = "ON" },
        ["bt_antenna_option"] = new(StringComparer.OrdinalIgnoreCase) { ["0"] = "None", ["1"] = "Chip 0", ["2"] = "Chip 1", ["3"] = "Both" }
    };

    private static readonly string[] HtmlDeclaredPaths =
    {
        "ac.address","ac.force_network","ac.port","account_expire","adv_relay.adv_intval","adv_relay.filter_data","adv_relay.filter_exclude",
        "adv_relay.filter_type","adv_relay.gap_time","adv_relay.keep_time","allow-origin","azure.connstr","ble_power",
        "chip-params","conn_params.chip1.conn_max_intval","conn_params.chip1.conn_min_intval","conn_params.chip1.connect_interval_priority",
        "conn_params.chip1.data_length_ext","conn_params.chip1.latency","conn_params.chip1.latency_priority","conn_params.chip1.scan_intval",
        "conn_params.chip1.scan_window","conn_params.chip1.supervision_timeout_priority","conn_params.chip1.supvtimeout","conn_params.chip1.type",
        "conn_params.conn_max_intval","conn_params.conn_min_intval","conn_params.connect_interval_priority","conn_params.data_length_ext",
        "conn_params.latency","conn_params.latency_priority","conn_params.scan_intval","conn_params.scan_window","conn_params.supervision_timeout_priority",
        "conn_params.supvtimeout","conn_params.type","container.ssh-login","control_channel","ac_cert.ca_crt","ac_cert.crt","ac_cert.key","crtconfig",
        "dongle.apn","dongle.band_select","dongle.defaultroute","dongle.demand","dongle.device","dongle.dialnumber","dongle.dns",
        "dongle.dongle_recovery","dongle.ifname","dongle.ipv6","dongle.keepalive","dongle.maxwait","dongle.metric","dongle.mtu",
        "dongle.password","dongle.peerdns","dongle.pincode","dongle.proto","dongle.service","dongle.type","dongle.username",
        "fat","https","local_btstack","log_level","name","oauth-enable","port1","port2","port3","port4","proto1","proto2","proto3","proto4",
        "scan.chip1.filter_data","scan.chip1.filter_offset","scan.chip1.filter_rssi","scan.chip1.filter_type","scan.chip1.one_scan_time",
        "scan.chip1.scan_interval","scan.chip1.scan_window","scan.chip1.type","scan.filter_data","scan.filter_offset","scan.filter_rssi",
        "scan.filter_type","scan.one_scan_time","scan.scan_interval","scan.scan_window","ssh-login","start.bypass.ca","start.bypass.cache_max",
        "start.bypass.cert","start.bypass.connection_type","start.bypass.encryption_mode","start.bypass.heartbeat_interval","start.bypass.host",
        "start.bypass.identity","start.bypass.key","start.bypass.keyfilepassword","start.bypass.keyId","start.bypass.password","start.bypass.port",
        "start.bypass.psk","start.bypass.qos_of_scan","start.bypass.queueUrl","start.bypass.require_client_certificate","start.bypass.send_interval",
        "start.bypass.sqskey","start.bypass.topic_of_scan","start.bypass.use","start.bypass.username","start.connection.actions","start.connection.keep_time",
        "start.connection.period","start.connection.timeout","start.connection.use","start.notification.data_type","start.notification.timestamp",
        "start.notification.use","start.other.connection_list","start.other.connection_list_interval","start.scanning.chip","start.scanning.data_type",
        "start.scanning.filter_duplicates","start.scanning.filter_mac","start.scanning.filter_name","start.scanning.filter_rssi","start.scanning.filter_uuid",
        "start.scanning.filter_value.data","start.scanning.filter_value.offset","start.scanning.report_interval","start.scanning.timestamp","start.scanning.use","stat_interval","timeconf.auto","timeconf.ntp1",
        "timeconf.ntp2","timezone","tpm-enable","wifi_interference_ch_1","wifi_interference_ch_11","wifi_interference_ch_6","wifi_interference_ch_auto",
        "wired.dns1","wired.dns2","wired.gateway","wired.ip","wired.netmask","wired.proto","wireless.addnew","wireless.ca_cert","wireless.ca_hidden",
        "wireless.client_cert","wireless.country","wireless.dns1","wireless.dns2","wireless.eap","wireless.eap_password","wireless.encryption",
        "wireless.gateway","wireless.identity","wireless.ip","wireless.mode","wireless.netmask","wireless.password","wireless.priv_key","wireless.priv_key_pwd",
        "wireless.proto","wireless.ssid","wireless_ap.ca_hidden","wireless_ap.disabled","wireless_ap.ip","wireless_ap.netmask","wireless_ap.password",
        "wireless_ap.ssid","wireless_verify","wireless1.ca_cert","wireless1.client_cert","wireless1.dns1","wireless1.dns2","wireless1.eap",
        "wireless1.eap_password","wireless1.encryption","wireless1.identity","wireless1.password","wireless1.priv_key","wireless1.priv_key_pwd",
        "wireless1.proto","wireless1.ssid"
    };

    private static readonly Dictionary<string, string[]> PathAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["container.ssh-login"] = new[] { "container.ssh" },
        ["start.bypass.identity"] = new[] { "start.bypass.psk-identity" },
        ["tpm-enable"] = new[] { "tpm_enable" },
        ["chip-params"] = new[] { "chip" }
    };

    private static readonly Dictionary<string, int> HtmlPathOrder = HtmlDeclaredPaths
        .Select((path, index) => new { Path = NormalizePath(path), Index = index })
        .GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.Min(x => x.Index), StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string> KnownLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["name"] = "Gateway Name",
        ["fat"] = "Gateway Mode",
        ["wireless.country"] = "Country/Region",
        ["stat_interval"] = "Statistics Report Interval",
        ["control_channel"] = "AC-Gateway Protocol",
        ["oauth-enable"] = "Enable OAuth2 Token For Local API",
        ["ssh-login"] = "Remote Assistance",
        ["wireless_ap.disabled"] = "Enable",
        ["wireless_verify"] = "Verify Before Saving",
        ["start.connection.actions"] = "Connection Init Actions",
        ["start.scanning.filter_duplicates"] = "Duplicates Filter",
        ["start.scanning.report_interval"] = "Device Report Interval",
        ["local_btstack"] = "BLE Connection Mode",
        ["log_level"] = "Log Level",
        ["crtconfig"] = "MQTT Certificate Configuration",
        ["timeconf.ntp1"] = "NTP Server 1",
        ["timeconf.ntp2"] = "NTP Server 2",
        ["timeconf.now"] = "Gateway Time",
        ["show_model"] = "Model",
        ["mac"] = "MAC",
        ["version"] = "Firmware Version",
        ["uptime"] = "Up Time (seconds)",
        ["wired.iface.ip"] = "ETH IP",
        ["wireless_ap.iface.ip"] = "WLAN AP IP",
        ["dongle.iface.ip"] = "Cellular IP"
    };

    private static readonly HashSet<string> CheckboxPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "wireless_ap.ca_hidden",
        "wireless.ca_hidden",
        "wireless1.ca_hidden",
        "wifi_interference_ch_1",
        "wifi_interference_ch_6",
        "wifi_interference_ch_11",
        "wifi_interference_ch_auto",
        "timeconf.auto",
        "account_expire",
        "https",
        "tpm-enable"
    };

    private static readonly HashSet<string> IpLikePaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "wired.ip", "wired.netmask", "wired.gateway", "wired.dns1", "wired.dns2",
        "wireless_ap.ip", "wireless_ap.netmask", "wireless.ip", "wireless.netmask", "wireless.gateway", "wireless.dns1", "wireless.dns2",
        "wireless1.dns1", "wireless1.dns2", "ac.address"
    };

    private readonly MainViewModel _main;
    private JsonObject _latestSettings = new();
    private JsonObject _latestSaveTemplate = new();
    private string? _latestCsrf;
    private readonly Dictionary<string, CassiaEditableSettingItem> _itemsByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CassiaEditableSettingItem> _trackedItems = new();
    private bool _applyingRules;

    public CassiaSettingsViewModel(MainViewModel main, string cassiaName)
    {
        _main = main ?? throw new ArgumentNullException(nameof(main));
        CassiaName = (cassiaName ?? "").Trim();
        WindowTitle = $"Cassia Web Settings - {CassiaName}";
        InitializeTabs();
        _ = Refresh();
    }

    public string CassiaName { get; }
    public string WindowTitle { get; }

    [ObservableProperty] private string gatewayIp = "192.168.40.1";
    [ObservableProperty] private string username = "admin";
    [ObservableProperty] private string password = "Kode1234!";
    [ObservableProperty] private string passwordEncrypted = DefaultEncryptedPassword;
    [ObservableProperty] private string statusText = "Idle";
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string rawSettingsJson = "{}";
    [ObservableProperty] private string rawSavePayloadPreviewJson = "{}";
    [ObservableProperty] private CassiaSettingsTab? selectedTab;

    public ObservableCollection<CassiaSettingsTab> NavigationTabs { get; } = new();

    public event Action? RequestClose;

    [RelayCommand]
    private async Task Refresh()
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusText = "Loading cassia settings...";

        try
        {
            var result = await _main.RequestCassiaSettingsAsync(
                CassiaName,
                GatewayIp,
                Username,
                Password,
                PasswordEncrypted);

            ApplyResult(result, "Loaded");
        }
        catch (Exception ex)
        {
            StatusText = "Load failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task Save()
    {
        if (IsBusy) return;
        IsBusy = true;

        try
        {
            if (!ValidateCurrentInput(out var validationError))
            {
                StatusText = validationError;
                return;
            }

            var payload = BuildSavePayloadFromUi();
            RawSavePayloadPreviewJson = ToPrettyJson(payload);
            if (!ContainsSettingChanges(payload))
            {
                StatusText = "No changed settings to save.";
                return;
            }

            StatusText = "Saving cassia settings to all Cassias in network...";
            var result = await _main.SaveCassiaSettingsAsync(
                "all",
                payload,
                GatewayIp,
                Username,
                Password,
                PasswordEncrypted);

            ApplyResult(result, "Saved");
        }
        catch (Exception ex)
        {
            StatusText = "Save failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Close() => RequestClose?.Invoke();

    private void InitializeTabs()
    {
        NavigationTabs.Clear();
        foreach (var key in TabOrder)
            NavigationTabs.Add(new CassiaSettingsTab(key, TabTitles[key]));
        SelectedTab = NavigationTabs.FirstOrDefault();
    }

    private void ApplyResult(CassiaSettingsCommandResult? result, string successPrefix)
    {
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() => ApplyResult(result, successPrefix));
            return;
        }

        if (result == null)
        {
            StatusText = "No response from gateway.";
            return;
        }

        if (!string.IsNullOrWhiteSpace(result.GatewayIp))
            GatewayIp = result.GatewayIp;

        _latestSettings = result.Settings as JsonObject ?? new JsonObject();
        _latestSaveTemplate = result.SavePayloadTemplate as JsonObject ?? new JsonObject();
        _latestCsrf = result.Csrf;

        BuildUiFromSettings(_latestSettings, _latestSaveTemplate);
        RawSettingsJson = ToPrettyJson(_latestSettings);
        RawSavePayloadPreviewJson = ToPrettyJson(BuildSavePayloadFromUi());

        var msg = string.IsNullOrWhiteSpace(result.Message)
            ? (result.Success ? $"{successPrefix} cassia settings." : "Cassia settings command failed.")
            : result.Message;
        StatusText = result.Success ? msg : $"Failed: {msg}";
    }

    private void BuildUiFromSettings(JsonObject settings, JsonObject saveTemplate)
    {
        UntrackItems();
        _itemsByPath.Clear();
        InitializeTabs();

        var rawPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        FlattenLeafPaths(settings, null, rawPaths);
        FlattenLeafPaths(saveTemplate, null, rawPaths);

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawPath in rawPaths)
            paths.Add(NormalizePath(rawPath));
        foreach (var required in HtmlDeclaredPaths)
            paths.Add(NormalizePath(required));

        foreach (var path in paths
                     .Where(p => !p.Equals("csrf", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(p => HtmlPathOrder.TryGetValue(p, out var order) ? 0 : 1)
                     .ThenBy(p => HtmlPathOrder.TryGetValue(p, out var order) ? order : int.MaxValue)
                     .ThenBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var node = GetPathWithAliases(settings, path) ?? GetPathWithAliases(saveTemplate, path);
            var (tabKey, section) = MapPathToNavigation(path);
            var selectOptions = GetSelectOptions(path, node);
            var item = new CassiaEditableSettingItem(
                path,
                GetLabel(path),
                node,
                InferEditor(path, node, selectOptions.Count > 0),
                selectOptions,
                isReadOnly: IsReadOnlyPath(path));
            AddItem(tabKey, section, item);
            if (!string.IsNullOrWhiteSpace(path))
                _itemsByPath[path] = item;
        }

        AddBtAntennaOption(settings, saveTemplate);

        AddStatusVirtualFields(settings);
        TrackItems();
        ApplyConditionalRules();
        EnsureTabFallbackContent();
    }

    private void AddStatusVirtualFields(JsonObject settings)
    {
        var modeText = ReadString(settings, "fat") switch
        {
            "0" => "AC Managed Gateway",
            "1" => "Standalone Gateway",
            "2" => "Repeater Gateway",
            _ => "Unknown"
        };

        AddItem("status", "Overview", new CassiaEditableSettingItem(
            "",
            "Working Mode",
            JsonValue.Create(modeText),
            CassiaFieldEditorKind.ReadOnly,
            isReadOnly: true));

        var uptimeRaw = ReadString(settings, "uptime");
        if (long.TryParse(uptimeRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sec))
        {
            var span = TimeSpan.FromSeconds(sec);
            var text = $"{(int)span.TotalDays}d {span.Hours}h {span.Minutes}m {span.Seconds}s";
            AddItem("status", "Resources", new CassiaEditableSettingItem(
                "",
                "Up Time (formatted)",
                JsonValue.Create(text),
                CassiaFieldEditorKind.ReadOnly,
                isReadOnly: true));
        }
    }

    private void AddBtAntennaOption(JsonObject settings, JsonObject saveTemplate)
    {
        var chip0 = ReadString(settings, "bt_antenna.chip0") ?? ReadString(saveTemplate, "bt_antenna.chip0");
        var chip1 = ReadString(settings, "bt_antenna.chip1") ?? ReadString(saveTemplate, "bt_antenna.chip1");
        var option = BtAntennaToOption(chip0, chip1);
        var options = GetSelectOptions("bt_antenna_option", JsonValue.Create(option));
        var item = new CassiaEditableSettingItem(
            "bt_antenna_option",
            "External Antenna",
            JsonValue.Create(option),
            CassiaFieldEditorKind.Select,
            options,
            isReadOnly: false);

        AddItem("basic", "Gateway", item);
        _itemsByPath["bt_antenna_option"] = item;
    }

    private void EnsureTabFallbackContent()
    {
        var eventsTab = NavigationTabs.FirstOrDefault(t => t.Key.Equals("events", StringComparison.OrdinalIgnoreCase));
        if (eventsTab != null && eventsTab.Sections.Count == 0)
        {
            var section = new CassiaSettingsSection("Events");
            section.Items.Add(new CassiaEditableSettingItem(
                "",
                "The HTML Events tab is runtime log streaming. It is not part of /cassia/info JSON settings.",
                JsonValue.Create(""),
                CassiaFieldEditorKind.ReadOnly,
                isReadOnly: true));
            eventsTab.Sections.Add(section);
        }

        foreach (var tab in NavigationTabs)
        {
            if (tab.Sections.Count > 0) continue;
            var section = new CassiaSettingsSection("Info");
            section.Items.Add(new CassiaEditableSettingItem(
                "",
                "No settings found for this tab.",
                JsonValue.Create(""),
                CassiaFieldEditorKind.ReadOnly,
                isReadOnly: true));
            tab.Sections.Add(section);
        }
    }

    private void AddItem(string tabKey, string sectionName, CassiaEditableSettingItem item)
    {
        var tab = NavigationTabs.FirstOrDefault(t => t.Key.Equals(tabKey, StringComparison.OrdinalIgnoreCase));
        if (tab == null) return;
        var section = tab.Sections.FirstOrDefault(s => s.Title.Equals(sectionName, StringComparison.OrdinalIgnoreCase));
        if (section == null)
        {
            section = new CassiaSettingsSection(sectionName);
            tab.Sections.Add(section);
        }

        section.Items.Add(item);
    }

    private void TrackItems()
    {
        foreach (var item in GetAllItems())
        {
            _trackedItems.Add(item);
            item.PropertyChanged += OnItemPropertyChanged;
        }
    }

    private void UntrackItems()
    {
        foreach (var item in _trackedItems)
            item.PropertyChanged -= OnItemPropertyChanged;
        _trackedItems.Clear();
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_applyingRules)
            return;

        if (e.PropertyName != nameof(CassiaEditableSettingItem.ValueText) &&
            e.PropertyName != nameof(CassiaEditableSettingItem.BoolValue))
            return;

        ApplyConditionalRules();
        RawSavePayloadPreviewJson = ToPrettyJson(BuildSavePayloadFromUi());
    }

    private void ApplyConditionalRules()
    {
        _applyingRules = true;
        try
        {
            foreach (var item in _itemsByPath.Values)
            {
                item.IsVisible = true;
                item.IsEnabled = item.IsEditable;
            }

            var fat = GetText("fat");
            var model = GetText("model");
            if (string.IsNullOrWhiteSpace(model))
                model = GetText("show_model");
            var isStandalone = fat.Equals("1", StringComparison.OrdinalIgnoreCase);
            var isManagedOrStandalone = fat.Equals("0", StringComparison.OrdinalIgnoreCase) || isStandalone;
            var isRepeater = fat.Equals("2", StringComparison.OrdinalIgnoreCase);
            var isX2000Family = IsX2000FamilyModel(model);
            var isS2000 = model.Equals("S2000", StringComparison.OrdinalIgnoreCase);

            SetVisible("wireless.country", isStandalone);
            SetVisibleByPrefix("adv_relay.", isRepeater);
            SetVisibleByPrefix("wired.", isManagedOrStandalone, excludePrefix: "wired.iface.");
            SetVisibleByPrefix("wireless_ap.", isManagedOrStandalone, excludePrefix: "wireless_ap.iface.");
            SetVisibleByPrefix("wireless.", isStandalone);
            SetVisibleByPrefix("wireless1.", isStandalone);
            SetVisible("wireless_verify", isStandalone);
            SetVisibleByPrefix("dongle.", isManagedOrStandalone);
            SetVisibleByPrefix("start.", isStandalone);
            SetVisible("bt_antenna_option", true);
            SetVisible("tpm-enable", true);
            SetEnabled("bt_antenna_option", isX2000Family);
            SetEnabled("tpm-enable", isX2000Family);

            var wiredStatic = GetText("wired.proto").Equals("static", StringComparison.OrdinalIgnoreCase);
            SetEnabled("wired.ip", wiredStatic);
            SetEnabled("wired.netmask", wiredStatic);
            SetEnabled("wired.gateway", wiredStatic);

            var apEnabled = !GetText("wireless_ap.disabled").Equals("1", StringComparison.OrdinalIgnoreCase);
            SetEnabled("wireless_ap.ssid", apEnabled);
            SetEnabled("wireless_ap.ca_hidden", apEnabled);
            SetEnabled("wireless_ap.password", apEnabled);
            SetEnabled("wireless_ap.ip", apEnabled);
            SetEnabled("wireless_ap.netmask", apEnabled);

            ApplyWirelessRules();
            ApplyServiceRules();
            ApplyContainerRules();
            ApplyOtherRules(isS2000);
        }
        finally
        {
            _applyingRules = false;
        }
    }

    private void ApplyWirelessRules()
    {
        var mode = GetText("wireless.mode");
        var isSta = mode.Equals("sta", StringComparison.OrdinalIgnoreCase);
        var isAp = mode.Equals("ap", StringComparison.OrdinalIgnoreCase);
        var isDisabled = mode.Equals("disable", StringComparison.OrdinalIgnoreCase);
        var sec = GetText("wireless.encryption");
        var isPsk = sec.Equals("psk2", StringComparison.OrdinalIgnoreCase) ||
                    sec.Equals("psk-mixed+tkip+aes", StringComparison.OrdinalIgnoreCase);
        var isEnterprise = sec.Equals("wpa2", StringComparison.OrdinalIgnoreCase) ||
                           sec.Equals("wpa-mixed+tkip+aes", StringComparison.OrdinalIgnoreCase);
        var eap = GetText("wireless.eap");
        var isTls = eap.Equals("tls", StringComparison.OrdinalIgnoreCase);
        var isTtls = eap.Equals("ttls-MSCHAPV2", StringComparison.OrdinalIgnoreCase) ||
                     eap.Equals("ttls-PAP", StringComparison.OrdinalIgnoreCase);

        SetVisible("wireless.ssid", !isDisabled);
        SetVisible("wireless.ca_hidden", !isDisabled);
        SetVisible("wireless.encryption", !isDisabled);
        SetVisible("wireless.password", isAp || isPsk);
        SetVisible("wireless.eap", isSta && isEnterprise);
        SetVisible("wireless.identity", isSta && isEnterprise);
        SetVisible("wireless.eap_password", isSta && isEnterprise);
        SetVisible("wireless.ca_cert", isSta && isEnterprise && (isTls || isTtls));
        SetVisible("wireless.client_cert", isSta && isEnterprise && isTls);
        SetVisible("wireless.priv_key", isSta && isEnterprise && isTls);
        SetVisible("wireless.priv_key_pwd", isSta && isEnterprise && isTls);
        SetVisible("wireless.proto", isSta);

        var wirelessStatic = isSta && GetText("wireless.proto").Equals("static", StringComparison.OrdinalIgnoreCase);
        SetVisible("wireless.ip", wirelessStatic);
        SetVisible("wireless.netmask", wirelessStatic);
        SetVisible("wireless.gateway", wirelessStatic);
        SetVisible("wireless.dns1", isSta);
        SetVisible("wireless.dns2", isSta);
        SetVisible("wireless.addnew", isSta);

        var useSecondary = isSta && GetText("wireless.addnew").Equals("yes", StringComparison.OrdinalIgnoreCase);
        SetVisibleByPrefix("wireless1.", useSecondary);

        var sec1 = GetText("wireless1.encryption");
        var isPsk1 = sec1.Equals("psk2", StringComparison.OrdinalIgnoreCase) ||
                     sec1.Equals("psk-mixed+tkip+aes", StringComparison.OrdinalIgnoreCase);
        var isEnterprise1 = sec1.Equals("wpa2", StringComparison.OrdinalIgnoreCase) ||
                            sec1.Equals("wpa-mixed+tkip+aes", StringComparison.OrdinalIgnoreCase);
        var eap1 = GetText("wireless1.eap");
        var isTls1 = eap1.Equals("tls", StringComparison.OrdinalIgnoreCase);
        var isTtls1 = eap1.Equals("ttls-MSCHAPV2", StringComparison.OrdinalIgnoreCase) ||
                      eap1.Equals("ttls-PAP", StringComparison.OrdinalIgnoreCase);

        SetVisible("wireless1.password", useSecondary && isPsk1);
        SetVisible("wireless1.eap", useSecondary && isEnterprise1);
        SetVisible("wireless1.identity", useSecondary && isEnterprise1);
        SetVisible("wireless1.eap_password", useSecondary && isEnterprise1);
        SetVisible("wireless1.ca_cert", useSecondary && isEnterprise1 && (isTls1 || isTtls1));
        SetVisible("wireless1.client_cert", useSecondary && isEnterprise1 && isTls1);
        SetVisible("wireless1.priv_key", useSecondary && isEnterprise1 && isTls1);
        SetVisible("wireless1.priv_key_pwd", useSecondary && isEnterprise1 && isTls1);
    }

    private void ApplyServiceRules()
    {
        var use = GetText("start.bypass.use");
        var isMqtt = use.Equals("mqtt", StringComparison.OrdinalIgnoreCase);
        var isSqs = use.Equals("sqs", StringComparison.OrdinalIgnoreCase);

        var mqttOnly = new[]
        {
            "start.bypass.host","start.bypass.port","start.bypass.connection_type","start.bypass.heartbeat_interval",
            "start.bypass.username","start.bypass.password","start.bypass.topic_of_scan","start.bypass.qos_of_scan",
            "start.bypass.encryption_mode"
        };
        foreach (var path in mqttOnly)
            SetVisible(path, isMqtt);

        SetVisible("start.bypass.keyId", isSqs);
        SetVisible("start.bypass.sqskey", isSqs);
        SetVisible("start.bypass.queueUrl", isSqs);
        SetEnabled("start.bypass.queueUrl", isSqs);

        var encryptionMode = GetText("start.bypass.encryption_mode");
        var isPsk = isMqtt && encryptionMode.Equals("psk", StringComparison.OrdinalIgnoreCase);
        var isCert = isMqtt && encryptionMode.Equals("certificate", StringComparison.OrdinalIgnoreCase);
        SetVisible("start.bypass.psk", isPsk);
        SetVisible("start.bypass.identity", isPsk);
        SetVisible("start.bypass.ca", isCert);
        SetVisible("start.bypass.require_client_certificate", isCert);

        var requiresClientCert = isCert && GetText("start.bypass.require_client_certificate").Equals("yes", StringComparison.OrdinalIgnoreCase);
        SetVisible("start.bypass.cert", requiresClientCert);
        SetVisible("start.bypass.key", requiresClientCert);
        SetVisible("start.bypass.keyfilepassword", requiresClientCert);

        var periodicConnection = GetText("start.connection.use").Equals("period", StringComparison.OrdinalIgnoreCase);
        SetEnabled("start.connection.period", periodicConnection);
        SetEnabled("start.connection.keep_time", periodicConnection);

        var connectionListPeriodic = GetText("start.other.connection_list").Equals("2", StringComparison.OrdinalIgnoreCase);
        SetEnabled("start.other.connection_list_interval", connectionListPeriodic);
    }

    private void ApplyContainerRules()
    {
        SetEnabled("port1", !string.IsNullOrWhiteSpace(GetText("proto1")));
        SetEnabled("port2", !string.IsNullOrWhiteSpace(GetText("proto2")));
        SetEnabled("port3", !string.IsNullOrWhiteSpace(GetText("proto3")));
        SetEnabled("port4", !string.IsNullOrWhiteSpace(GetText("proto4")));
    }

    private void ApplyOtherRules(bool isS2000)
    {
        var autoWifiAvoid = GetBool("wifi_interference_ch_auto");
        SetEnabled("wifi_interference_ch_1", !autoWifiAvoid);
        SetEnabled("wifi_interference_ch_6", !autoWifiAvoid);
        SetEnabled("wifi_interference_ch_11", !autoWifiAvoid);

        var dongleType = GetText("dongle.type");
        SetEnabled("dongle.dongle_recovery", !dongleType.Equals("none", StringComparison.OrdinalIgnoreCase));
        SetVisible("dongle.dongle_recovery", !dongleType.Equals("none", StringComparison.OrdinalIgnoreCase) && !isS2000);

        var chipMode = GetText("chip-params");
        var model = GetText("model");
        if (string.IsNullOrWhiteSpace(model))
            model = GetText("show_model");
        var isSModel = model.StartsWith("S", StringComparison.OrdinalIgnoreCase);
        SetEnabled("scan.one_scan_time", chipMode.Equals("3", StringComparison.OrdinalIgnoreCase) && isSModel);
        SetEnabled("scan.filter_type", chipMode.Equals("4", StringComparison.OrdinalIgnoreCase));
        var chip0FilterType = GetText("scan.filter_type");
        SetEnabled("scan.filter_offset", chipMode.Equals("4", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(chip0FilterType));
        SetEnabled("scan.filter_data", chipMode.Equals("4", StringComparison.OrdinalIgnoreCase));

        var chip1ScanMode = GetText("scan.chip1.type");
        SetEnabled("scan.chip1.one_scan_time", chip1ScanMode.Equals("3", StringComparison.OrdinalIgnoreCase) && isSModel);
        SetEnabled("scan.chip1.filter_type", chip1ScanMode.Equals("4", StringComparison.OrdinalIgnoreCase));
        var chip1FilterType = GetText("scan.chip1.filter_type");
        SetEnabled("scan.chip1.filter_offset", chip1ScanMode.Equals("4", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(chip1FilterType));
        SetEnabled("scan.chip1.filter_data", chip1ScanMode.Equals("4", StringComparison.OrdinalIgnoreCase));
    }

    private void SetVisible(string path, bool visible)
    {
        if (!_itemsByPath.TryGetValue(path, out var item))
            return;

        if (ShowAllFields)
        {
            item.IsVisible = true;
            if (!visible)
                item.IsEnabled = false;
            return;
        }

        item.IsVisible = visible;
    }

    private void SetEnabled(string path, bool enabled)
    {
        if (_itemsByPath.TryGetValue(path, out var item))
            item.IsEnabled = item.IsEditable && enabled;
    }

    private void SetVisibleByPrefix(string prefix, bool visible, string? excludePrefix = null)
    {
        foreach (var (path, item) in _itemsByPath)
        {
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!string.IsNullOrWhiteSpace(excludePrefix) &&
                path.StartsWith(excludePrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            if (ShowAllFields)
            {
                item.IsVisible = true;
                if (!visible)
                    item.IsEnabled = false;
            }
            else
            {
                item.IsVisible = visible;
            }
        }
    }

    private string GetText(string path)
    {
        if (_itemsByPath.TryGetValue(path, out var item))
            return (item.ValueText ?? "").Trim();
        return "";
    }

    private bool GetBool(string path)
    {
        var text = GetText(path);
        return text.Equals("1", StringComparison.OrdinalIgnoreCase) ||
               text.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               text.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
               text.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsX2000FamilyModel(string? model)
    {
        var value = (model ?? "").Trim();
        return value.Equals("X2000", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("iWAPXN3-ATX2000", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("IIBH2-ATX2000-CN", StringComparison.OrdinalIgnoreCase);
    }

    private JsonObject BuildSavePayloadFromUi()
    {
        var payload = new JsonObject();
        foreach (var item in GetAllItems().Where(i => i.IsEditable && i.IsVisible && i.IsChanged))
        {
            if (item.Path.Equals("bt_antenna_option", StringComparison.OrdinalIgnoreCase))
            {
                ApplyBtAntennaOptionToPayload(payload, item.ValueText);
                continue;
            }

            SetPath(payload, item.Path, ParseItemValue(item));
        }

        var csrf = !string.IsNullOrWhiteSpace(_latestCsrf)
            ? _latestCsrf
            : ReadString(_latestSaveTemplate, "csrf") ?? ReadString(_latestSettings, "csrf");
        if (!string.IsNullOrWhiteSpace(csrf))
            payload["csrf"] = csrf;

        return payload;
    }

    private static void ApplyBtAntennaOptionToPayload(JsonObject payload, string? optionText)
    {
        var option = (optionText ?? "").Trim();
        var chip0 = "0";
        var chip1 = "0";

        if (option.Equals("1", StringComparison.OrdinalIgnoreCase))
            chip0 = "1";
        else if (option.Equals("2", StringComparison.OrdinalIgnoreCase))
            chip1 = "1";
        else if (option.Equals("3", StringComparison.OrdinalIgnoreCase))
        {
            chip0 = "1";
            chip1 = "1";
        }

        SetPath(payload, "bt_antenna.chip0", chip0);
        SetPath(payload, "bt_antenna.chip1", chip1);
    }

    private IEnumerable<CassiaEditableSettingItem> GetAllItems() =>
        NavigationTabs.SelectMany(t => t.Sections).SelectMany(s => s.Items);

    private bool ValidateCurrentInput(out string error)
    {
        foreach (var item in GetAllItems().Where(i => i.IsEditable && i.IsVisible && i.IsChanged))
        {
            var value = (item.ValueText ?? "").Trim();
            if (item.Path.Equals("name", StringComparison.OrdinalIgnoreCase) && !NameRegex.IsMatch(value))
            {
                error = "Gateway name is invalid. Allowed: letters/numbers/underscore/space/dot/slash, max 30 chars.";
                return false;
            }

            if (IpLikePaths.Contains(item.Path) && !string.IsNullOrWhiteSpace(value) && !Ipv4Regex.IsMatch(value))
            {
                error = $"'{item.Label}' must be a valid IPv4 address.";
                return false;
            }

            if ((item.OriginalNode is JsonObject || item.OriginalNode is JsonArray) && item.IsChanged)
            {
                try
                {
                    _ = JsonNode.Parse(value);
                }
                catch
                {
                    error = $"'{item.Label}' must contain valid JSON.";
                    return false;
                }
            }

            if (item.IsSelectEditor && item.HasAllowedValues &&
                !item.AllowedValues.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                error = $"'{item.Label}' must be one of: {item.AllowedValuesText}.";
                return false;
            }
        }

        error = "";
        return true;
    }

    private static object? ParseItemValue(CassiaEditableSettingItem item)
    {
        if (item.IsCheckBoxEditor)
        {
            var b = item.BoolValue ?? false;
            if (item.OriginalNode is JsonValue original)
            {
                if (original.TryGetValue<bool>(out _)) return b;
                if (original.TryGetValue<int>(out _)) return b ? 1 : 0;
                if (original.TryGetValue<long>(out _)) return b ? 1L : 0L;
            }

            return b ? "1" : "0";
        }

        return ParseAdvancedValue(item.ValueText, item.OriginalNode);
    }

    private static CassiaFieldEditorKind InferEditor(string path, JsonNode? node, bool hasSelectOptions)
    {
        if (IsReadOnlyPath(path)) return CassiaFieldEditorKind.ReadOnly;
        if (CheckboxPaths.Contains(path)) return CassiaFieldEditorKind.CheckBox;
        if (hasSelectOptions) return CassiaFieldEditorKind.Select;
        if (node is JsonObject || node is JsonArray) return CassiaFieldEditorKind.TextArea;
        if (path.Contains("password", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".psk", StringComparison.OrdinalIgnoreCase))
            return CassiaFieldEditorKind.Password;
        if (path.EndsWith("cert", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("crt", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("key", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("ca", StringComparison.OrdinalIgnoreCase))
            return CassiaFieldEditorKind.TextArea;
        if (node is JsonValue v && v.TryGetValue<bool>(out _))
            return CassiaFieldEditorKind.CheckBox;
        return CassiaFieldEditorKind.Text;
    }

    private static bool IsReadOnlyPath(string path)
    {
        return path.StartsWith("version", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("show_model", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("model", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("mac", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("uptime", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("cpu.", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("mem.", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("disk.", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("mqtt_stat.", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("chipinfo.", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("wired.iface.", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("wireless_ap.iface.", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("SN", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("features", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("features.", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("timeconf.now", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<CassiaSelectOption> GetSelectOptions(string path, JsonNode? node)
    {
        if (!KnownAllowedValues.TryGetValue(path, out var values) || values.Length == 0)
            return Array.Empty<CassiaSelectOption>();

        KnownOptionLabels.TryGetValue(path, out var labels);
        var current = ReadNodeAsString(node);

        var result = new List<CassiaSelectOption>(values.Length + 1);
        foreach (var value in values)
        {
            var key = value ?? "";
            var label = labels != null && labels.TryGetValue(key, out var mapped) ? mapped : key;
            result.Add(new CassiaSelectOption(key, label));
        }

        if (!string.IsNullOrWhiteSpace(current) &&
            !result.Any(o => o.Value.Equals(current, StringComparison.OrdinalIgnoreCase)))
        {
            result.Add(new CassiaSelectOption(current, current));
        }

        return result;
    }

    private static string ReadNodeAsString(JsonNode? node)
    {
        if (node is not JsonValue value)
            return "";

        if (value.TryGetValue<string>(out var s))
            return (s ?? "").Trim();
        if (value.TryGetValue<bool>(out var b))
            return b ? "1" : "0";
        if (value.TryGetValue<int>(out var i))
            return i.ToString(CultureInfo.InvariantCulture);
        if (value.TryGetValue<long>(out var l))
            return l.ToString(CultureInfo.InvariantCulture);
        if (value.TryGetValue<double>(out var d))
            return d.ToString(CultureInfo.InvariantCulture);
        return "";
    }

    private static string BtAntennaToOption(string? chip0, string? chip1)
    {
        var c0 = (chip0 ?? "").Trim();
        var c1 = (chip1 ?? "").Trim();

        var b0 = c0.Equals("1", StringComparison.OrdinalIgnoreCase);
        var b1 = c1.Equals("1", StringComparison.OrdinalIgnoreCase);
        if (b0 && b1) return "3";
        if (b0) return "1";
        if (b1) return "2";
        return "0";
    }

    private static string GetLabel(string path)
    {
        if (KnownLabels.TryGetValue(path, out var label))
            return label;

        var last = path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? path;
        last = last.Replace("-", " ").Replace("_", " ");
        return string.Join(" ", last.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(ToTitleCaseWord));
    }

    private static string ToTitleCaseWord(string value)
    {
        if (value.Length == 0) return value;
        if (value.Length == 1) return value.ToUpperInvariant();
        return char.ToUpperInvariant(value[0]) + value[1..];
    }

    private static (string TabKey, string Section) MapPathToNavigation(string path)
    {
        if (IsReadOnlyPath(path)) return ("status", "Resources");
        if (path is "name" or "fat" or "wireless.country" or "ble_power" or "stat_interval" or "control_channel") return ("basic", "Gateway");
        if (path.StartsWith("adv_relay.", StringComparison.OrdinalIgnoreCase)) return ("basic", "Repeater");
        if (path.StartsWith("ac.", StringComparison.OrdinalIgnoreCase) || path is "oauth-enable" or "ssh-login") return ("basic", "AC");
        if (path.StartsWith("wired.", StringComparison.OrdinalIgnoreCase)) return ("basic", "Wired");
        if (path.StartsWith("wireless_ap.", StringComparison.OrdinalIgnoreCase)) return ("basic", "Wireless AP");
        if (path.StartsWith("wireless1.", StringComparison.OrdinalIgnoreCase)) return ("basic", "Secondary Wi-Fi");
        if (path.StartsWith("wireless.", StringComparison.OrdinalIgnoreCase) || path.Equals("wireless_verify", StringComparison.OrdinalIgnoreCase)) return ("basic", "Wireless");
        if (path.StartsWith("dongle.", StringComparison.OrdinalIgnoreCase)) return ("basic", "Cellular Modem");

        if (path.StartsWith("start.bypass.", StringComparison.OrdinalIgnoreCase)) return ("service", "Service Access");
        if (path.StartsWith("start.scanning.", StringComparison.OrdinalIgnoreCase)) return ("service", "Scanning");
        if (path.StartsWith("start.connection.", StringComparison.OrdinalIgnoreCase)) return ("service", "Connection");
        if (path.StartsWith("start.notification.", StringComparison.OrdinalIgnoreCase)) return ("service", "Notification");
        if (path.StartsWith("start.other.", StringComparison.OrdinalIgnoreCase)) return ("service", "Other");

        if (path.StartsWith("container.", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("proto", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("port", StringComparison.OrdinalIgnoreCase))
            return ("container", "Container");

        if (path.StartsWith("local_btstack", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("wifi_interference_", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("scan.", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("conn_params.", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("bt_antenna.", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("azure.", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("timeconf.", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("timezone", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("ac_cert.", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("crtconfig", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("log_level", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("https", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("tpm-enable", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("allow-origin", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("local-api", StringComparison.OrdinalIgnoreCase))
            return ("other", "Advanced");

        return ("other", "Advanced");
    }

    private static bool ContainsSettingChanges(JsonObject payload)
    {
        foreach (var kv in payload)
        {
            if (kv.Key.Equals("csrf", StringComparison.OrdinalIgnoreCase))
                continue;
            return true;
        }

        return false;
    }

    private static object? ParseAdvancedValue(string? text, JsonNode? originalNode)
    {
        var raw = text ?? "";
        var value = raw.Trim();

        if (originalNode is JsonObject || originalNode is JsonArray)
        {
            try
            {
                return JsonNode.Parse(raw);
            }
            catch
            {
                return raw;
            }
        }

        if (originalNode is JsonValue original)
        {
            if (original.TryGetValue<string>(out _)) return raw;
            if (original.TryGetValue<bool>(out _))
            {
                var parsed = ParseLooseBool(value);
                return parsed.HasValue ? parsed.Value : raw;
            }
            if (original.TryGetValue<int>(out _))
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) return i;
                return raw;
            }
            if (original.TryGetValue<long>(out _))
            {
                if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;
                return raw;
            }
            if (original.TryGetValue<double>(out _))
            {
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
                return raw;
            }
        }

        if (value.Equals("null", StringComparison.OrdinalIgnoreCase)) return null;
        var boolGuess = ParseLooseBool(value);
        if (boolGuess.HasValue) return boolGuess.Value;
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longGuess)) return longGuess;
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleGuess)) return doubleGuess;
        return raw;
    }

    private static bool? ParseLooseBool(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        if (value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("on", StringComparison.OrdinalIgnoreCase))
            return true;
        if (value.Equals("0", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("false", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("no", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("off", StringComparison.OrdinalIgnoreCase))
            return false;
        return null;
    }

    private static void FlattenLeafPaths(JsonNode? node, string? prefix, ISet<string> target)
    {
        if (node is JsonObject obj)
        {
            if (obj.Count == 0 && !string.IsNullOrWhiteSpace(prefix))
            {
                target.Add(prefix);
                return;
            }

            foreach (var kv in obj)
            {
                var childPath = string.IsNullOrWhiteSpace(prefix) ? kv.Key : $"{prefix}.{kv.Key}";
                FlattenLeafPaths(kv.Value, childPath, target);
            }
            return;
        }

        if (node is JsonArray)
        {
            if (!string.IsNullOrWhiteSpace(prefix))
                target.Add(prefix);
            return;
        }
        if (!string.IsNullOrWhiteSpace(prefix)) target.Add(prefix);
    }

    private static void SetPath(JsonObject root, string path, object? value)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var parts = path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return;

        JsonObject cur = root;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            var key = parts[i];
            if (cur[key] is JsonObject child)
            {
                cur = child;
            }
            else
            {
                child = new JsonObject();
                cur[key] = child;
                cur = child;
            }
        }

        cur[parts[^1]] = value switch
        {
            null => null,
            JsonNode node => node.DeepClone(),
            string s => s,
            bool b => b,
            int i => i,
            long l => l,
            double d => d,
            _ => value.ToString()
        };
    }

    private static string NormalizePath(string path)
    {
        var normalized = (path ?? "").Trim();
        if (normalized.Equals("container.ssh", StringComparison.OrdinalIgnoreCase))
            return "container.ssh-login";
        if (normalized.Equals("start.bypass.psk-identity", StringComparison.OrdinalIgnoreCase))
            return "start.bypass.identity";
        if (normalized.Equals("tpm_enable", StringComparison.OrdinalIgnoreCase))
            return "tpm-enable";
        if (normalized.Equals("chip", StringComparison.OrdinalIgnoreCase))
            return "chip-params";
        return normalized;
    }

    private static JsonNode? GetPath(JsonObject root, string path)
    {
        var parts = path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        JsonNode? cur = root;
        foreach (var p in parts)
        {
            if (cur is not JsonObject obj || !obj.TryGetPropertyValue(p, out cur))
                return null;
        }
        return cur;
    }

    private static JsonNode? GetPathWithAliases(JsonObject root, string path)
    {
        var normalized = NormalizePath(path);
        var node = GetPath(root, normalized);
        if (node != null)
            return node;

        if (PathAliases.TryGetValue(normalized, out var aliases))
        {
            foreach (var alias in aliases)
            {
                node = GetPath(root, alias);
                if (node != null)
                    return node;
            }
        }

        return null;
    }

    private static string? ReadString(JsonObject root, string path)
    {
        var node = GetPath(root, path);
        if (node == null) return null;
        return node switch
        {
            JsonValue value when value.TryGetValue<string>(out var s) => s,
            JsonValue value when value.TryGetValue<int>(out var i) => i.ToString(CultureInfo.InvariantCulture),
            JsonValue value when value.TryGetValue<long>(out var l) => l.ToString(CultureInfo.InvariantCulture),
            JsonValue value when value.TryGetValue<double>(out var d) => d.ToString(CultureInfo.InvariantCulture),
            JsonValue value when value.TryGetValue<bool>(out var b) => b ? "1" : "0",
            _ => node.ToJsonString()
        };
    }

    private static string ToPrettyJson(JsonNode? node)
    {
        if (node == null) return "{}";
        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
