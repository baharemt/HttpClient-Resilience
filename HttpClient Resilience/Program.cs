using System.Net.Http.Json;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Polly.CircuitBreaker;
using Polly.Timeout;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddHttpClient("InventoryReservations", client =>
    {
        client.BaseAddress = new Uri("http://localhost:5080/");
        client.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
    })
    .AddStandardResilienceHandler(options =>
    {
        options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(5);
        options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(1);

        options.Retry.MaxRetryAttempts = 2;
        options.Retry.Delay = TimeSpan.FromMilliseconds(200);
        options.Retry.BackoffType = DelayBackoffType.Exponential;
        options.Retry.UseJitter = true;

        options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(10);
        options.CircuitBreaker.MinimumThroughput = 4;
        options.CircuitBreaker.FailureRatio = 0.5;
        options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(10);
    });

var app = builder.Build();

app.MapPost("/reservations", async (
    ReserveRequest body,
    HttpRequest incoming,
    IHttpClientFactory factory,
    CancellationToken requestAborted) =>
{
    var keys = incoming.Headers["Idempotency-Key"];
    var key = keys.Count == 1 ? keys[0] : null;

    if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(body.Sku))
        return Results.BadRequest("Idempotency-Key and sku are required.");

    using var client = factory.CreateClient("InventoryReservations");

    // این named client فقط برای endpoint رزرو استفاده می‌شه.
    using var outgoing = new HttpRequestMessage(HttpMethod.Post, "reservations")
    {
        Content = JsonContent.Create(body)
    };
    outgoing.Headers.Add("Idempotency-Key", key);

    try
    {
        using var response = await client.SendAsync(outgoing, requestAborted);

        var content = await response.Content.ReadAsStringAsync(requestAborted);
        return Results.Content(
            content,
            response.Content.Headers.ContentType?.ToString(),
            statusCode: (int)response.StatusCode);
    }
    catch (BrokenCircuitException)
    {
        return Results.Problem(
            title: "Inventory circuit is open",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (TimeoutRejectedException)
    {
        return Results.Problem(
            title: "Inventory request timed out",
            statusCode: StatusCodes.Status504GatewayTimeout);
    }
    catch (HttpRequestException)
    {
        return Results.Problem(
            title: "Could not get a response from Inventory API",
            statusCode: StatusCodes.Status502BadGateway);
    }
});

app.Run();

record ReserveRequest(string Sku);