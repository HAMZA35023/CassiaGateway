namespace AccessAPP.Services
{
    public class CassiaReadWriteService
    {
        private readonly HttpClient httpClient;
        public CassiaReadWriteService()
        {
            httpClient = new HttpClient();
        }

        public async Task<HttpResponseMessage> WriteBleMessage(string gatewayIpAddress, string macAddress, int handle, string hexLoginValue, string queryParams)
        {
            try
            {
                string endpoint = $"http://{gatewayIpAddress}/gatt/nodes/{macAddress}/handle/{handle}/value/{hexLoginValue}{queryParams}";

                var request = new HttpRequestMessage(HttpMethod.Get, endpoint);

                using (var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();

                    // You can add additional processing logic here if needed
                    return response;
                }


            }
            catch (Exception ex)
            {
                throw new Exception($"Error: {ex.Message}");
            }
        }
    }
}
