using Azure;
using Azure.Identity;
using Common;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication()
    .ConfigureServices(services =>
    {
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();
        services.AddAzureClients(clientBuilder =>
        {
            var blobServiceClient = clientBuilder.AddBlobServiceClient(Environment.GetEnvironmentVariable("STG_CONN")).WithCredential(new DefaultAzureCredential());
            var eventGridPublisherClient = clientBuilder.AddEventGridPublisherClient(new Uri("https://disssevents.northeurope-1.eventgrid.azure.net/api/events"), new AzureKeyCredential(Environment.GetEnvironmentVariable("EVNT_GRD_PUB_KYE")));
        });


        services.AddOptions<List<MatchConfiguration>>()
        .Configure<IConfiguration>((settings, configuration) =>
        {
            configuration.GetSection("matchConfigurations").Bind(settings);
        });

        services.AddHttpClient();
    })
    .ConfigureAppConfiguration((context, config) =>
    {
        config.AddJsonFile($"appsettings.json", optional: false, reloadOnChange: false);
    })
    .Build();

host.Run();
