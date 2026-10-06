using Khors.App.Services;
using Khors.App.ViewModels;
using Khors.Engines.Latency;
using Xunit;

namespace Khors.App.Tests;

/// <summary>Строка профиля: код страны из флага в имени и уровень задержки для цвета и полосок.</summary>
public class ProfileRowTests
{
    [Theory]
    [InlineData("🇩🇪 Германия", "DE", "Германия")]
    [InlineData("Финляндия 🇫🇮", "FI", "Финляндия")]
    [InlineData("Сервер 🇳🇱 Амстердам", "NL", "Сервер Амстердам")]
    [InlineData("🇺🇸🇬🇧 Мульти", "US", "🇬🇧 Мульти")]
    public void SplitsFirstFlag(string name, string code, string rest)
    {
        var (actualCode, actualName) = CountryFlag.Split(name);

        Assert.Equal(code, actualCode);
        Assert.Equal(rest, actualName);
    }

    [Theory]
    [InlineData("Мой сервер")]
    [InlineData("")]
    [InlineData("🔥 Быстрый")]
    public void NoFlagKeepsName(string name)
    {
        var (code, rest) = CountryFlag.Split(name);

        Assert.Null(code);
        Assert.Equal(name, rest);
    }

    [Fact]
    public void FlagOnlyKeepsOriginalName()
    {
        var (code, rest) = CountryFlag.Split("🇩🇪");

        Assert.Equal("DE", code);
        Assert.Equal("🇩🇪", rest);
    }

    [Theory]
    [InlineData(40, LatencyLevel.Good)]
    [InlineData(150, LatencyLevel.Good)]
    [InlineData(151, LatencyLevel.Mid)]
    [InlineData(400, LatencyLevel.Mid)]
    [InlineData(900, LatencyLevel.Slow)]
    public void LatencyLevelByDelay(int milliseconds, LatencyLevel expected) =>
        Assert.Equal(expected, ProfileItemViewModel.LevelOf(LatencyResult.Success(TimeSpan.FromMilliseconds(milliseconds))));

    [Fact]
    public void LatencyLevelForFailureAndUntested()
    {
        Assert.Equal(LatencyLevel.Failed, ProfileItemViewModel.LevelOf(new LatencyResult(LatencyStatus.Timeout)));
        Assert.Equal(LatencyLevel.None, ProfileItemViewModel.LevelOf(null));
    }
}
