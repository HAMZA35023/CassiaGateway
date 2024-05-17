using System.Net;

namespace AccessAPP.Models
{
    public class ResponseModel
    {
        public string MacAddress { get; set; }
        public string Data { get; set; }
        public long Time { get; set; }
        public int Retries { get; set; }
        public HttpStatusCode Status { get; set; }

        public bool PincodeRequired { get; set; }
        public bool PinCodeAccepted { get; set; }
    }
}
