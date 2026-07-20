using Microsoft.Extensions.Configuration;
using Naudit.Core.Review;
using Naudit.Infrastructure.Settings;
using Xunit;

namespace Naudit.Tests;

public class ReviewResolutionOptionsTests
{
    [Fact]
    public void Defaults_areOn()
    {
        var o = new ReviewResolutionOptions();
        Assert.True(o.Enabled);
        Assert.True(o.LlmClassification);
        Assert.True(o.RenderCheckbox);
    }

    [Fact]
    public void BindsFromConfig_underReviewResolution()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Naudit:Review:Resolution:Enabled"] = "false",
        }).Build();
        var opts = cfg.GetSection("Naudit:Review").Get<ReviewOptions>()!;
        Assert.False(opts.Resolution.Enabled);
    }

    [Fact]
    public void CatalogContainsResolutionKeys_allNonSecret()
    {
        foreach (var key in new[]
        {
            "Naudit:Review:Resolution:Enabled",
            "Naudit:Review:Resolution:LlmClassification",
            "Naudit:Review:Resolution:RenderCheckbox",
        })
        {
            Assert.True(SettingsCatalog.TryGet(key, out var def), $"{key} fehlt im Katalog");
            Assert.False(def!.IsSecret);
        }
    }
}
