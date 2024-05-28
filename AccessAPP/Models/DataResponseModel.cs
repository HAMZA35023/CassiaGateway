using System.Net;

namespace AccessAPP.Models
{
    public class DataResponseModel
    {
        public string MacAddress { get; set; }
        public string Data { get; set; }
        public long Time { get; set; }
        public HttpStatusCode Status { get; set; }
    }
}
