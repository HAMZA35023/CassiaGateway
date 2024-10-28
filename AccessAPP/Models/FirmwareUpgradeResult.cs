using System.Net;

namespace AccessAPP.Models
{
    public class FirmwareUpgradeResult
    {
        public string MacAddress { get; set; }
        public string Status { get; set; }
        public HttpStatusCode ErrorCode { get; set; }
        public string Message { get; set; }
    }
}
