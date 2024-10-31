using Azure.Messaging;
using Azure.Messaging.EventGrid;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;
using Common;
using FuzzySharp;
using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;
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
            // Start the orchestration for this blob file
            string instanceId = await starter.ScheduleNewOrchestrationInstanceAsync(nameof(Orchestrator), new OrchestratorPayload { BlobName = name });
        }

        [Function(nameof(Orchestrator))]
        public static async Task RunOrchestrator(
            [OrchestrationTrigger] TaskOrchestrationContext context)
        {
            ILogger logger = context.CreateReplaySafeLogger(nameof(Orchestrator));

            var blobName = context.GetInput<OrchestratorPayload>()?.BlobName;
            var transcribedResponse = await context.CallActivityAsync<WhisperResponseDto>(nameof(PostAudioSnippetAsync), blobName);
            await context.CallActivityAsync(nameof(FindMatchesAsync), transcribedResponse);

        }

        [Function(nameof(PostAudioSnippetAsync))]
        public async Task<WhisperResponseDto> PostAudioSnippetAsync([ActivityTrigger] string blobName, FunctionContext executionContext)
        {
            var containerClient = blobServiceClient.GetBlobContainerClient(BLOB_CONTAINER_NAME);
            var blob = containerClient.GetBlockBlobClient(blobName);
            var file = await blob.DownloadContentAsync();

            using var client = httpClientFactory.CreateClient();

            try
            {
                var memoryStream = new MemoryStream();
                await file.Value.Content.ToStream().CopyToAsync(memoryStream);
                memoryStream.Position = 0;

                var fileContent = new ByteArrayContent(memoryStream.ToArray());
                var form = new MultipartFormDataContent
                {
                    { fileContent, "file", blobName }
                };

                var response = await client.PostAsync(API_URL_EUROPE, form);
                response.EnsureSuccessStatusCode();


                var responseContent = await response.Content.ReadAsStringAsync();
                var respDto = JsonSerializer.Deserialize<WhisperResponseDto>(responseContent);
                var eventData = new CloudEvent("/disssfunction", EventTypes.Transcribed.ToString(), respDto);
                await eventGridPublisherClient.SendEventAsync(eventData);

                return respDto;

            }
            catch (TimeoutException tEx)
            {
                throw;
            }
            catch (HttpRequestException reqEx)
            {
                throw;

            }
            catch (Exception ex) {
                throw;
            }
            finally
            {
                await blob.DeleteAsync();
            }
        }

        [Function(nameof(FindMatchesAsync))]
        public async Task FindMatchesAsync([ActivityTrigger] WhisperResponseDto result, FunctionContext executionContext)
        {
            var transcribedWords = result.text.Split(" ").ToList();
            const int threshold = 70;

            var matchDict = new Dictionary<string, int>();

            foreach (var transcribedWord in transcribedWords)
            {
                foreach (var matchGroup in matchConfiguration.Value)
                {
                    foreach (var keyWord in matchGroup.KeyWords)
                    {
                        var matchRatio = Fuzz.Ratio(transcribedWord, keyWord);
                        Console.WriteLine($"{keyWord} matches with {transcribedWord}, ratio {matchRatio}");

                        if (matchRatio <= threshold) continue;

                        if (matchDict.TryGetValue(matchGroup.GroupKey, out var currentMatch))
                        {
                            matchDict[matchGroup.GroupKey] = currentMatch++;
                        }
                        else
                        {
                            matchDict[matchGroup.GroupKey] = 1;
                        }
                    }
                }
            }

            if (matchDict.Count > 0)
            {
                var top3Matches = matchDict.OrderByDescending(x => x.Value).Take(3); 
                var maxMatch = matchDict.Aggregate((x, y) => x.Value > y.Value ? x : y);
                Console.WriteLine($"Group {maxMatch.Key} won with {maxMatch.Value}");
            }
           
        }
    }

    public class OrchestratorPayload
    {
        public string BlobName { get; set; }
    }
}
