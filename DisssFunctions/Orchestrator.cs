using Azure.Messaging;
using Azure.Messaging.EventGrid;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;
using Common;
using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace DisssFunctions
{
    public class Orchestrator
    {
        private readonly BlobServiceClient blobServiceClient;
        private readonly IHttpClientFactory httpClientFactory;
        private readonly EventGridPublisherClient eventGridPublisherClient;
        private readonly IOptions<List<MatchConfiguration>> matchConfiguration;
        private readonly IConfiguration Configuration;

        private const string API_URL = "https://containertest.wonderfultree-2fb2324d.australiaeast.azurecontainerapps.io/upload";
        private const string API_URL_EUROPE = "https://disssrunner.orangetree-569898da.northeurope.azurecontainerapps.io/upload";
        private const string BLOB_CONTAINER_NAME = "audiotransfer";

        public Orchestrator(BlobServiceClient blobServiceClient, IHttpClientFactory httpClientFactory, EventGridPublisherClient eventGridPublisherClient, IOptions<List<MatchConfiguration>> matchConfiguration)
        {
            this.blobServiceClient = blobServiceClient;
            this.httpClientFactory = httpClientFactory;
            this.eventGridPublisherClient = eventGridPublisherClient;
            this.matchConfiguration = matchConfiguration;
        }

        [Function("BlobTriggerFunction")]
        public static async Task Run([BlobTrigger("audiotransfer/{name}", Connection = "STG_CONN")] Stream myBlob, string name, [DurableClient] DurableTaskClient starter)
        {
            if (!name.EndsWith("_trimmed.wav"))
            {
                string instanceId = await starter.ScheduleNewOrchestrationInstanceAsync(nameof(Orchestrator), new OrchestratorPayload { BlobName = name });
            }
        }

        [Function(nameof(Orchestrator))]
        public static async Task RunOrchestrator(
            [OrchestrationTrigger] TaskOrchestrationContext context)
        {
            ILogger logger = context.CreateReplaySafeLogger(nameof(Orchestrator));

            var blobName = context.GetInput<OrchestratorPayload>()?.BlobName;
            var trimmedBlobName = await context.CallActivityAsync<string>(nameof(PreProcessAsync), blobName);
            if (!string.IsNullOrEmpty(trimmedBlobName))
            {
                var transcribedResponse = await context.CallActivityAsync<WhisperResponseDto>(nameof(PostAudioSnippetAsync), trimmedBlobName);
                var match = await context.CallActivityAsync<MatchReponseItemDto>(nameof(FindMatchesAsync), transcribedResponse);
                if (match != null) await context.CallActivityAsync(nameof(FindImageAsync), match);
            }
           

        }

        [Function(nameof(PreProcessAsync))]
        public async Task<string> PreProcessAsync([ActivityTrigger] string blobName, FunctionContext executionContext)
        {
            await eventGridPublisherClient.SendEventAsync(new SystemEvent($"Entered PreProcessAsync for {blobName}"));
            var containerClient = blobServiceClient.GetBlobContainerClient(BLOB_CONTAINER_NAME);
            var blob = containerClient.GetBlockBlobClient(blobName);
            var file = await blob.DownloadContentAsync();

            // Load audio data from the input file
            byte[] audioBytes = file.Value.Content.ToArray();
            try
            {
                // Set silence threshold and chunk size
                float silenceThreshold = -30.0f;
                int chunkSizeMs = 10;

                // Process the audio to trim silence and save the output
                (byte[] trimmedAudio, double secondsShavedOff) = AudioProcessor.TrimSilenceFromAudio(audioBytes, silenceThreshold, chunkSizeMs);
                if (trimmedAudio != null && trimmedAudio.Length > 0)
                {
                    var newBlobName = Path.GetFileNameWithoutExtension(blobName) + "_trimmed" + Path.GetExtension(blobName);

                    using (var stream = new MemoryStream(trimmedAudio, writable: false))
                    {
                        await containerClient.UploadBlobAsync(newBlobName, stream);
                    }
                    await eventGridPublisherClient.SendEventAsync(new SystemEvent($"PreProcessor shaved off {secondsShavedOff.ToString("F2")} seconds of silence"));
                    await blob.DeleteAsync();

                    return newBlobName;
                }
                else
                {
                    await eventGridPublisherClient.SendEventAsync(new SystemEvent($"PreProcessor claims audio was empty {blobName}"));
                    return string.Empty;
                }
            }
            catch (Exception ex)
            {
                await eventGridPublisherClient.SendEventAsync(new SystemEvent($"PreProcessor failed for {blobName}, continue with original"));
                return blobName;
            }
        }

        [Function(nameof(PostAudioSnippetAsync))]
        public async Task<WhisperResponseDto> PostAudioSnippetAsync([ActivityTrigger] string trimmedBlobName, FunctionContext executionContext)
        {
            await eventGridPublisherClient.SendEventAsync(new SystemEvent($"Entered PostAudioSnippedAsync for {trimmedBlobName}"));
            var containerClient = blobServiceClient.GetBlobContainerClient(BLOB_CONTAINER_NAME);
            var blob = containerClient.GetBlockBlobClient(trimmedBlobName);
            var file = await blob.DownloadContentAsync();
            byte[] audioBytes = file.Value.Content.ToArray();

            using var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(60);

            try
            {
                var memoryStream = new MemoryStream(audioBytes);
                memoryStream.Position = 0;

                var fileContent = new ByteArrayContent(memoryStream.ToArray());
                var form = new MultipartFormDataContent
                {
                    { fileContent, "file", trimmedBlobName }
                };

                var sw = new Stopwatch();
                sw.Start();
                var response = await client.PostAsync(API_URL_EUROPE, form);
                response.EnsureSuccessStatusCode();
                sw.Stop();
                await eventGridPublisherClient.SendEventAsync(new SystemEvent($"Whisper wrapper reply after {sw.Elapsed.TotalSeconds}s"));


                var responseContent = await response.Content.ReadAsStringAsync();
                var respDto = JsonSerializer.Deserialize<WhisperResponseDto>(responseContent);
                var eventData = new CloudEvent("/disssfunction", EventTypes.Transcribed.ToString(), respDto);
                await eventGridPublisherClient.SendEventAsync(eventData);

                return respDto;

            }
            catch (TimeoutException tEx)
            {
                await eventGridPublisherClient.SendEventAsync(new SystemEvent($"Ran into timeout calling wisper wrapper {tEx.Message}"));
                throw;
            }
            catch (HttpRequestException reqEx)
            {
                await eventGridPublisherClient.SendEventAsync(new SystemEvent($"Ran into http exception calling wisper wrapper {reqEx.Message}"));
                throw;
            }
            catch (Exception ex) {
                await eventGridPublisherClient.SendEventAsync(new SystemEvent($"Ran into generic exception calling wisper wrapper {ex.Message}"));
                throw;
            }
            finally
            {
                await blob.DeleteAsync();
            }

        }

        [Function(nameof(FindMatchesAsync))]
        public async Task<MatchReponseItemDto?> FindMatchesAsync([ActivityTrigger] WhisperResponseDto result, FunctionContext executionContext)
        {
            await eventGridPublisherClient.SendEventAsync(new SystemEvent("Entered FindMatchesAsync"));

            var transcribedWords = result.text
                .Split(" ")
                .Select(x => Regex.Replace(x, @"[^\w\s]", string.Empty))
                .ToList();

            var matchDict = new Dictionary<string, int>();

            foreach (var transcribedWord in transcribedWords)
            {
                foreach (var matchGroup in matchConfiguration.Value)
                {
                    foreach (var keyWord in matchGroup.KeyWords)
                    {
                        if (keyWord.Equals(transcribedWord, StringComparison.CurrentCultureIgnoreCase))
                        {
                            if (matchDict.TryGetValue(matchGroup.GroupKey, out var currentMatch))
                            {
                                matchDict[matchGroup.GroupKey]++;
                            }
                            else
                            {
                                matchDict[matchGroup.GroupKey] = 1;
                            }
                        }
                    }
                }
            }
            if (matchDict.Count > 0)
            {
                var topMatch = matchDict.OrderByDescending(x => x.Value).Select(i => new MatchReponseItemDto(i.Value, i.Key)).ToList().First();
                var eventData = new CloudEvent("/disssfunction", EventTypes.Matched.ToString(), new List<MatchReponseItemDto> { topMatch });
                await eventGridPublisherClient.SendEventAsync(eventData);
                return topMatch;
            }

            await eventGridPublisherClient.SendEventAsync(new SystemEvent("No matches found"));
            return null;

        }

        [Function(nameof(FindImageAsync))]
        public async Task FindImageAsync([ActivityTrigger] MatchReponseItemDto result, FunctionContext executionContext)
        {
            await eventGridPublisherClient.SendEventAsync(new SystemEvent("Entered FindImageAsync"));

            if (result == null || string.IsNullOrEmpty(result.Keyword)) return;
            Console.WriteLine("entering");
            try
            {
                using var client = httpClientFactory.CreateClient();
            
                var url = new Uri($"https://api.pexels.com/v1/search?query={result.Keyword} brand&per_page=1");
                client.DefaultRequestHeaders.Add("Authorization", Environment.GetEnvironmentVariable("PEXEL_KEY"));
                var pexelsReponse = await client.GetAsync(url);
                pexelsReponse.EnsureSuccessStatusCode();
                var rep = await pexelsReponse.Content.ReadAsStringAsync();
                var respDto = JsonSerializer.Deserialize<PexelsResponseDto>(rep);
                if (respDto?.total_results > 0)
                {
                    await eventGridPublisherClient.SendEventAsync(new SystemEvent($"Found a neat photo for {result.Keyword}"));
                    var eventData = new CloudEvent("/disssfunction", EventTypes.PhotoFound.ToString(), respDto.photos.First().src.medium);
                    await eventGridPublisherClient.SendEventAsync(eventData);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"failed with ex {ex.InnerException}");
                throw;
            }
        }
    }

    public class OrchestratorPayload
    {
        public string BlobName { get; set; }
    }

    public class SystemEvent(string msg) : CloudEvent("/disssfunction", EventTypes.SystemEvent.ToString(), msg)
    {
    }
}
