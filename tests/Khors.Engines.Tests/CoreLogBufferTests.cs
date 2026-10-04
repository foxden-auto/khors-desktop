using System.Text;
using Khors.Core.Diagnostics;
using Khors.Engines.Processes;
using Xunit;

namespace Khors.Engines.Tests;

public class CoreLogBufferTests
{
    private readonly SecretMasker _masker = new(Encoding.UTF8.GetBytes("log-test"));

    [Fact]
    public void LinesAreMaskedOnArrival()
    {
        var buffer = new CoreLogBuffer(_masker);
        CoreLogLine? raised = null;
        buffer.LineAdded += (_, line) => raised = line;

        buffer.Add(CoreLogSource.StandardOutput, "from 192.0.2.10:51234 accepted tcp:www.example.com:443 [socks -> proxy]");

        var stored = Assert.Single(buffer.Snapshot());
        Assert.DoesNotContain("192.0.2.10", stored.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("www.example.com", stored.Text, StringComparison.Ordinal);
        Assert.Contains("[socks -> proxy]", stored.Text, StringComparison.Ordinal);
        Assert.Equal(stored, raised);
    }

    [Fact]
    public void BufferKeepsOnlyLastLines()
    {
        var buffer = new CoreLogBuffer(_masker, capacity: 3);

        for (var i = 1; i <= 5; i++)
        {
            buffer.Add(CoreLogSource.StandardError, $"line {i}");
        }

        Assert.Equal(["line 3", "line 4", "line 5"], buffer.Snapshot().Select(l => l.Text));
        Assert.Equal(["line 4", "line 5"], buffer.Tail(2));
    }
}
