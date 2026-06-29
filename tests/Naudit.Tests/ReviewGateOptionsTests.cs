using Microsoft.Extensions.Configuration;
using Naudit.Core.Models;
using Naudit.Core.Review;
using Xunit;

namespace Naudit.Tests;

public class ReviewGateOptionsTests
{
    [Fact]
    public void Defaults_blockOnlyOnConfirmedHigh()
    {
        var gate = new ReviewOptions().Gate;

        Assert.Equal(FindingSeverity.High, gate.MinSeverity);
        Assert.Equal(ReviewConfidence.Medium, gate.MinConfidence);
    }

    [Fact]
    public void Gate_bindsFromConfiguration_withEnumNames()
    {
        // Genau der Bindungs-Pfad aus AddNauditInfrastructure: GetSection("Naudit:Review").Get<ReviewOptions>().
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Naudit:Review:Gate:MinSeverity"] = "Critical",
                ["Naudit:Review:Gate:MinConfidence"] = "High",
            })
            .Build();

        var options = config.GetSection("Naudit:Review").Get<ReviewOptions>()!;

        Assert.Equal(FindingSeverity.Critical, options.Gate.MinSeverity);
        Assert.Equal(ReviewConfidence.High, options.Gate.MinConfidence);
    }
}
