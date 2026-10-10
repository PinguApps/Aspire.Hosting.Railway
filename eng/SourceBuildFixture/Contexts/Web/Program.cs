var app = WebApplication.CreateBuilder(args).Build();
app.MapGet("/", () => $"RAILWAY_PROJECT_BUILD_{File.ReadAllText("proof.txt").Trim()}_{Environment.GetEnvironmentVariable("RUNTIME_PROOF")}");
app.MapGet("/health", () => "healthy");
app.Run();
