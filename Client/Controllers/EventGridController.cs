using Azure;
using Azure.Messaging;
using Client.Hubs;
using Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using System.Text.Json.Nodes;

namespace Client.Controllers
{
    [ApiController]  // Mark this as an API controller
    [Route("api/[controller]")]
    public class EventGridController : ControllerBase
    {
        private readonly IHubContext<DataHub> hubContext;
        public EventGridController(IHubContext<DataHub> hubContext)
        {
                this.hubContext = hubContext;
        }

        [HttpPost]
        [HttpOptions]
        public async Task<IActionResult> ReceiveEvent()
        {
            var data = await BinaryData.FromStreamAsync(Request.Body);
            CloudEvent[] cloudEvents = CloudEvent.ParseMany(data);

            foreach (var cloudEvent in cloudEvents)
            {
                switch (Enum.Parse(typeof(EventTypes), cloudEvent.Type))
                {
                    case EventTypes.Transcribed:
                        await hubContext.Clients.All.SendAsync("EventGridMessage", cloudEvent);
                        break;

                    case EventTypes.Matched:
                        break;
                }
            }

            return Ok();
        }
    }
}
