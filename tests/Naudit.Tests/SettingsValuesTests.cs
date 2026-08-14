using Microsoft.Extensions.Configuration;
using Naudit.Infrastructure.Settings;
using Xunit;

namespace Naudit.Tests;

/// <summary>Der eine Ort, an dem sich Listen-Keys anders lesen als Skalare: CSV ⇄ indizierte
/// Config-Keys, plus die Env-Erkennung (Naudit__Sast__Analyzers__0 setzt KEINEN Elternwert).</summary>
public class SettingsValuesTests
{
    private static readonly SettingDefinition ListDef =
        new("Naudit:Sast:Analyzers", false, IsList: true);
    private static readonly SettingDefinition ScalarDef = new("Naudit:Ai:Model", false);

    private static IConfiguration Config(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.ToDictionary(p => p.Key, p => (string?)p.Value))
            .Build();

    [Fact]
    public void Normalize_trimmtUndVerwirftLeereEintraege()
    {
        Assert.Equal("opengrep,trivy", SettingsValues.Normalize(" opengrep , ,trivy "));
        Assert.Equal("", SettingsValues.Normalize("  ,  "));
    }

    [Fact]
    public void Read_liesListeAusIndiziertenKeysAlsCsv()
    {
        var config = Config(("Naudit:Sast:Analyzers:0", "opengrep"), ("Naudit:Sast:Analyzers:1", "trivy"));
        Assert.Equal("opengrep,trivy", SettingsValues.Read(config, ListDef));
    }

    [Fact]
    public void Read_ungesetzteListe_istNull()
        => Assert.Null(SettingsValues.Read(Config(), ListDef));

    [Fact]
    public void Read_skalar_liestDenKeyDirekt()
        => Assert.Equal("m", SettingsValues.Read(Config(("Naudit:Ai:Model", "m")), ScalarDef));

    [Fact]
    public void IsSet_liste_erkenntIndizierteKinderOhneElternwert()
    {
        var config = Config(("Naudit:Sast:Analyzers:0", "trivy"));
        Assert.Null(config["Naudit:Sast:Analyzers"]);   // genau die Falle
        Assert.True(SettingsValues.IsSet(config, ListDef));
        Assert.False(SettingsValues.IsSet(Config(), ListDef));
    }

    [Fact]
    public void Katalog_kenntSastListe()
    {
        Assert.True(SettingsCatalog.TryGet("Naudit:Sast:Enabled", out _));
        Assert.True(SettingsCatalog.TryGet("Naudit:Sast:Analyzers", out var analyzers));
        Assert.True(analyzers.IsList);
        Assert.Contains("opengrep", analyzers.AllowedValues!);
        Assert.Contains("dotnet-sca", analyzers.AllowedValues!);
    }
}
