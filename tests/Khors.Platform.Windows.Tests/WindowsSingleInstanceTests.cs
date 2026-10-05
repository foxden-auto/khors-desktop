using Xunit;

namespace Khors.Platform.Windows.Tests;

public class WindowsSingleInstanceTests
{
    // Имя Mutex общее для сессии: тест не запускать параллельно с работающим KHORS Desktop.
    [Fact]
    public async Task SecondInstanceSignalsTheFirst()
    {
        using var first = new WindowsSingleInstance();
        Assert.SkipUnless(first.IsFirst, "Запущен KHORS Desktop — тест единственного экземпляра пропущен");
        var activated = new TaskCompletionSource();
        first.ActivationRequested += (_, _) => activated.TrySetResult();

        await Task.Run(() =>
        {
            // Mutex принадлежит потоку: второй экземпляр создаём в другом потоке, как в другом процессе.
            using var second = new WindowsSingleInstance();
            Assert.False(second.IsFirst);
            second.SignalFirstInstance();
        }, TestContext.Current.CancellationToken);

        await activated.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
    }
}
