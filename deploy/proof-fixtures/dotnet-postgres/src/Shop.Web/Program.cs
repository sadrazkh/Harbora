using Shop.Data;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// The way a .NET project asks for its database: by name, from configuration. appsettings.json says
// localhost, as every template's does, so what answers here is whatever was put over it from outside.
app.MapGet("/", async (IConfiguration configuration) => Results.Json(new
{
    viaConnectionString = await Database.SelectOneAsync(configuration.GetConnectionString("DefaultConnection")),
    runtime = Environment.Version.ToString()
}));

app.Run();
