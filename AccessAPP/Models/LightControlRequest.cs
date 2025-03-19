namespace AccessAPP.Models
{
    public class LightControlRequest
    {
        public string MacAddress { get; set; }    // MAC Address of the BLE device
        public string HexLoginValue { get; set; } // Telegram string in hex format

    }
}
