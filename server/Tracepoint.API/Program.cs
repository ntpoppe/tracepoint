var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/", () => "-");
app.MapGet("/health", () => "OK");

app.Run();
