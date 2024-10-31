using Azure.Storage.Blobs;
using Microsoft.AspNetCore.SignalR;

namespace Client.Hubs
{
    public class DataHub : Hub
    {
        private readonly BlobServiceClient blobServiceClient;
        private const string BLOB_CONTAINER_NAME = "audiotransfer";

        public DataHub(BlobServiceClient blobServiceClient)
        {
            this.blobServiceClient = blobServiceClient;
        }

        public async Task SendData(string base64AudioMessage)
        {
            byte[] audioBytes = Convert.FromBase64String(base64AudioMessage);

            // Generate a unique filename for the audio (could also use user info)
            string fileName = $"{Guid.NewGuid()}.wav";

            var containerClient = blobServiceClient.GetBlobContainerClient(BLOB_CONTAINER_NAME);
            using (var stream = new MemoryStream(audioBytes, writable: false))
            {
                await containerClient.UploadBlobAsync(fileName, stream);
            }

            //string filePath = Path.Combine("wwwroot", "audio", fileName);
            //await File.WriteAllBytesAsync(filePath, audioBytes);
        }

        public async Task SendEventGridMessage(string message)
        {
            await Clients.All.SendAsync("EventGridMessage", message);
        }
    }
}
