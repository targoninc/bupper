using System.Text.Json;
using BupperLibrary;
using BupperLibrary.Models.Settings;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

string configurationFileName = IoHelper.GetConfigDirectory() + "/bupper-options.json";

builder.Configuration.SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile(configurationFileName, optional: true, reloadOnChange: true);

WebApplication app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.MapGet("/getConfig", () => app.Configuration.Get<BupperSettings>())
    .WithName("GetConfig")
    .WithOpenApi();

app.MapPut("/setConfig", (BupperSettings settings) =>
    {
        File.WriteAllText(configurationFileName, JsonSerializer.Serialize(settings, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
        return Results.Ok();
    })
    .WithName("SetConfig")
    .WithOpenApi();

app.Run();
