using Bupper;
using Bupper.Models;
using Bupper.Models.Settings;
using NJsonSchema;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

const string configurationFileName = "bupper-options.json";

builder.Configuration.SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile(configurationFileName, optional: true, reloadOnChange: true);

builder.Services.Configure<BupperSettings>(builder.Configuration);
builder.Services.AddHostedService<Worker>();

JsonSchema schema = JsonSchema.FromType<BupperSettings>();
string schemaJson = schema.ToJson();
File.WriteAllText("bupper-schema.json", schemaJson);

IHost host = builder.Build();
host.Run();
