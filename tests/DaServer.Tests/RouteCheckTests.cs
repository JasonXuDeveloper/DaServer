
namespace DaServer.Tests;

/// <summary>Route completeness (N4): messages nothing handles and requests two service types answer are found before a cluster runs.</summary>
public class RouteCheckTests
{
    [Test]
    public void MessagesWithoutAHandlerAndAmbiguousRequestsAreReported()
    {
        var problems = RouteCheck.Problems(typeof(Counter).Assembly);
        Assert.That(problems, Has.Some.StartsWith(typeof(Unhandled).FullName + ": no actor or service"));
        Assert.That(problems, Has.Some.StartsWith(typeof(AnsweredTwice).FullName + ": a request answered by 2 service types"));
        Assert.That(problems, Has.None.StartsWith(typeof(Add).FullName + ":"), "a message with its handler");
        Assert.That(problems, Has.None.StartsWith(typeof(GoldChanged).FullName + ":"), "a [ToClient] message: clients handle it");
        Assert.Throws<InvalidOperationException>(() => RouteCheck.Check(typeof(Counter).Assembly));
    }
}
