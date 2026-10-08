using System.Text.Json;
using Assistant.Core;
using Assistant.Core.Agent;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAssistantCore(builder.Configuration);
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins("http://localhost:5173").AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
app.UseCors();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

// Streams agent events to the UI as Server-Sent Events.
app.MapPost("/api/chat", async (ChatRequest req, IAgentRunner agent, HttpContext http, CancellationToken ct) =>
{
    http.Response.ContentType = "text/event-stream";
    http.Response.Headers.CacheControl = "no-cache";
    await foreach (var evt in agent.RunAsync(req.Prompt, ct))
    {
        var payload = JsonSerializer.Serialize(evt, evt.GetType());
        await http.Response.WriteAsync($"event: {evt.GetType().Name}\ndata: {payload}\n\n", ct);
        await http.Response.Body.FlushAsync(ct);
    }
});

app.Run();

public sealed record ChatRequest(string Prompt);

public partial class Program;
