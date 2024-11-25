public class CassiaReadWriteService : IDisposable
{
    private static readonly HttpClient httpClient = new HttpClient();
    private static SemaphoreSlim semaphore = new SemaphoreSlim(5); // Limit to 5 concurrent connections

    public async Task<HttpResponseMessage> WriteBleMessage(string gatewayIpAddress, string macAddress, int handle, string hexValue, string queryParams)
    {
        await semaphore.WaitAsync(); // Wait until it's safe to proceed
        try
        {
            string endpoint = $"http://{gatewayIpAddress}/gatt/nodes/{macAddress}/handle/{handle}/value/{hexValue}{queryParams}";
            var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            using (var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                return response;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message + ex.StackTrace}");
            throw new Exception($"Error: {ex.Message + ex.StackTrace}");
        }
        finally
        {
            semaphore.Release(); // Release the semaphore
        }
    }

    public void Dispose()
    {
        httpClient?.Dispose();
    }
}
