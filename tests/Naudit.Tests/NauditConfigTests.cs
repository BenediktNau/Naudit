using Microsoft.Extensions.Configuration;
using Naudit.Infrastructure.Settings;
using Xunit;

namespace Naudit.Tests;

/// <summary>Precedence-Vertrag: appsettings.json < DB-Settings < User-Secrets/Env.
/// EnvOverrides enthält genau die Quellen oberhalb der DB (fürs "via environment"-Lock der UI).</summary>
public sealed class NauditConfigTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("naudit-config").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WriteJson(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void DbUeberschreibtAppsettings_EnvUeberschreibtDb()
    {
        var builder = new ConfigurationBuilder();
        builder.AddJsonFile(WriteJson("appsettings.json",
            """{ "Naudit": { "Ai": { "Provider": "Ollama", "Model": "llama3.1" }, "Git": { "Platform": "GitLab" } } }"""));
        builder.AddInMemoryCollection(new Dictionary<string, string?>   // simuliert Env-Vars (liegt NACH appsettings)
        {
            ["Naudit:Git:Platform"] = "GitHub",
        });

        var env = NauditConfig.InsertDbSettings(builder, new Dictionary<string, string?>
        {
            ["Naudit:Ai:Provider"] = "Anthropic",   // DB schlägt appsettings
            ["Naudit:Git:Platform"] = "GitLab",     // Env schlägt DB
        });
        var config = builder.Build();

        Assert.Equal("Anthropic", config["Naudit:Ai:Provider"]); // DB gewinnt über appsettings
        Assert.Equal("llama3.1", config["Naudit:Ai:Model"]);     // appsettings bleibt sichtbar
        Assert.Equal("GitHub", config["Naudit:Git:Platform"]);   // Env gewinnt über DB

        Assert.NotNull(env.Root["Naudit:Git:Platform"]);  // env-gesperrt
        Assert.Null(env.Root["Naudit:Ai:Provider"]);      // nur DB ⇒ nicht gesperrt
        Assert.Null(env.Root["Naudit:Ai:Model"]);         // nur appsettings ⇒ nicht gesperrt
    }

    [Fact]
    public void OhneAppsettingsQuellen_landetDbGanzUnten()
    {
        var builder = new ConfigurationBuilder();
        builder.AddInMemoryCollection(new Dictionary<string, string?> { ["A"] = "env" });
        var env = NauditConfig.InsertDbSettings(builder, new Dictionary<string, string?> { ["A"] = "db", ["B"] = "db" });
        var config = builder.Build();
        Assert.Equal("env", config["A"]);
        Assert.Equal("db", config["B"]);
        Assert.NotNull(env.Root["A"]);
        Assert.Null(env.Root["B"]);
    }
}
