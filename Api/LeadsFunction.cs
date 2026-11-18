using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace SpectrumTattoo.Api;

public class LeadsFunction(ILogger<LeadsFunction> logger)
{
    public sealed record ConsultationRequest(
        string Name,
        string Phone,
        string Email,
        string PreferredArtist,
        string ServiceType,
        string Placement,
        string ApproximateSize,
        string IdeaDescription);

    [Function("leads")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "leads")] HttpRequestData req)
    {
        ConsultationRequest? payload;
        try
        {
            payload = await JsonSerializer.DeserializeAsync<ConsultationRequest>(
                req.Body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Invalid consultation request payload.");
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteAsJsonAsync(new { ok = false, error = "Invalid request body." });
            return bad;
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.Name) || string.IsNullOrWhiteSpace(payload.Phone))
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteAsJsonAsync(new { ok = false, error = "Name and phone number are required." });
            return bad;
        }

        logger.LogInformation(
            "Spectrum Tattoo consultation request received for {Artist} ({Service}) from {Name}.",
            payload.PreferredArtist,
            payload.ServiceType,
            payload.Name);

        var ok = req.CreateResponse(HttpStatusCode.OK);
        await ok.WriteAsJsonAsync(new
        {
            ok = true,
            message = "Consultation request received. Our studio will follow up during studio hours (Wed–Sun)."
        });
        return ok;
    }
}
