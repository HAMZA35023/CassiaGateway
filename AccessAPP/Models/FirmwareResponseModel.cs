namespace AccessAPP.Models
{
    public class FirmwareResponseModel
    {
        public string Status {  get; set; }
        public string Message { get; set; }

        public object Data { get; set; }    
    }
}
