namespace AccessAPP.Models
{
    public class ServiceResponse
    {
        public bool Success { get; set; }
        public int StatusCode { get; set; }
        public string Message { get; set; }
        public string MacAddress { get; set; }
    }

}
