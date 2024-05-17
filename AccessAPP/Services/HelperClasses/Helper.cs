using AccessAPP.Models;
using System.Net;

namespace AccessAPP.Services
{
    public static class Helper
    {
        
        public static ResponseModel CreateResponse(string macAddress, dynamic result)
        {
            return new ResponseModel
            {
                Status = result.StatusCode,
                MacAddress = macAddress,
                Data = result.StatusCode.Equals(200) ? "OK" : "Failed",
                Time = DateTimeOffset.Now.ToUnixTimeMilliseconds()
            };
        }

        public static ResponseModel CreateResponseWithMessage(string macAddress, dynamic result, string msg, bool pincodeRequired)
        {
            //int retries = result.retries;
            HttpStatusCode status = result.StatusCode;

            ResponseModel response = new()
            {
                MacAddress = macAddress,
                Data = msg,
                Time = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
                Retries = 1,
                Status = (HttpStatusCode)status,
                PincodeRequired = pincodeRequired
            };

            return response;
        }

    }
}
