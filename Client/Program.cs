using Azure.Identity;
using Client;
using Client.Hubs;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSignalR(opt => opt.MaximumReceiveMessageSize = 10 * 1024 * 1024);

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowEventGrid",
        builder => builder.AllowAnyOrigin()
                          .AllowAnyMethod()
                          .AllowAnyHeader());
});

builder.Services.AddAzureClients(clientBuilder =>
{
    var blobServiceClient = clientBuilder.AddBlobServiceClient(Environment.GetEnvironmentVariable("STG_CONN")).WithCredential(new DefaultAzureCredential());
});

// Add services to the container.
builder.Services.AddControllersWithViews().AddNewtonsoftJson();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
}
app.UseStaticFiles();

app.UseOptions();

app.UseRouting();

app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.MapHub<DataHub>("/datahub");

app.Run();
