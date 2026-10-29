using FeteadApi.Domain.Models.Monitoring;
using FeteadApi.Infrastructure.Repositories.Monitoring;

namespace FeteadApi.Monitoring.Tests;

/// <summary>Filtres du tableau DataJobs (sans base de données : on vérifie le SQL produit).</summary>
public class MonitoringRepositoryFilterTests
{
    [Fact]
    public void No_filter_adds_no_condition()
    {
        Assert.Equal("", MonitoringRepository.BuildJobFilterSql(new JobFilter()));
    }

    [Fact]
    public void Id_filter_is_an_exact_match()
    {
        var sql = MonitoringRepository.BuildJobFilterSql(new JobFilter { JobId = 1024 });

        Assert.Equal(" AND j.id = @JobId", sql);
    }

    [Fact]
    public void Name_filter_uses_ilike_with_a_parameter()
    {
        var sql = MonitoringRepository.BuildJobFilterSql(new JobFilter { Name = "alm" });

        Assert.Equal(" AND j.name ILIKE @NamePattern", sql);
    }

    [Fact]
    public void Both_filters_can_be_combined()
    {
        var sql = MonitoringRepository.BuildJobFilterSql(new JobFilter { JobId = 1, Name = "alm" });

        Assert.Equal(" AND j.id = @JobId AND j.name ILIKE @NamePattern", sql);
    }

    [Theory]
    [InlineData("ALM", "%ALM%")]
    [InlineData("ALM_RUN", "%ALM\\_RUN%")]   // "_" tapé par l'utilisateur = un vrai "_"
    [InlineData("100%", "%100\\%%")]         // "%" tapé par l'utilisateur = un vrai "%"
    [InlineData("a\\b", "%a\\\\b%")]
    public void Name_is_searched_anywhere_and_special_characters_are_taken_literally(string typed, string expected)
    {
        Assert.Equal(expected, MonitoringRepository.ToLikePattern(typed));
    }
}
