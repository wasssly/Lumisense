using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Lumisense;
using Xunit;

namespace Lumisense.Tests;

public sealed class UiExceptionRecoveryPolicyTests
{
    [Fact]
    public void Classify_IgnoresCancellation()
    {
        Assert.Equal(UiExceptionRecoveryAction.Ignore, UiExceptionRecoveryPolicy.Classify(new OperationCanceledException()));
        Assert.Equal(UiExceptionRecoveryAction.Ignore, UiExceptionRecoveryPolicy.Classify(new TaskCanceledException()));
    }

    [Fact]
    public void Classify_ContinuesOnExpectedEnvironmentErrors()
    {
        Assert.Equal(UiExceptionRecoveryAction.Continue, UiExceptionRecoveryPolicy.Classify(new IOException()));
        Assert.Equal(UiExceptionRecoveryAction.Continue, UiExceptionRecoveryPolicy.Classify(new FileNotFoundException()));
        Assert.Equal(UiExceptionRecoveryAction.Continue, UiExceptionRecoveryPolicy.Classify(new DirectoryNotFoundException()));
        Assert.Equal(UiExceptionRecoveryAction.Continue, UiExceptionRecoveryPolicy.Classify(new UnauthorizedAccessException()));
        Assert.Equal(UiExceptionRecoveryAction.Continue, UiExceptionRecoveryPolicy.Classify(new FormatException()));
        Assert.Equal(UiExceptionRecoveryAction.Continue, UiExceptionRecoveryPolicy.Classify(new InvalidDataException()));
        Assert.Equal(UiExceptionRecoveryAction.Continue, UiExceptionRecoveryPolicy.Classify(new HttpRequestException()));
    }

    [Fact]
    public void Classify_TerminatesOnUnexpectedErrors()
    {
        Assert.Equal(UiExceptionRecoveryAction.Terminate, UiExceptionRecoveryPolicy.Classify(new InvalidOperationException()));
        Assert.Equal(UiExceptionRecoveryAction.Terminate, UiExceptionRecoveryPolicy.Classify(new ArgumentException()));
        Assert.Equal(UiExceptionRecoveryAction.Terminate, UiExceptionRecoveryPolicy.Classify(new NullReferenceException()));
        Assert.Equal(UiExceptionRecoveryAction.Terminate, UiExceptionRecoveryPolicy.Classify(new NotSupportedException()));
    }

    [Fact]
    public void Classify_LooksInsideAggregateWithSingleInnerException()
    {
        var wrappedIo = new AggregateException(new IOException());
        var wrappedTwice = new AggregateException(new AggregateException(new OperationCanceledException()));
        var wrappedBug = new AggregateException(new InvalidOperationException());

        Assert.Equal(UiExceptionRecoveryAction.Continue, UiExceptionRecoveryPolicy.Classify(wrappedIo));
        Assert.Equal(UiExceptionRecoveryAction.Ignore, UiExceptionRecoveryPolicy.Classify(wrappedTwice));
        Assert.Equal(UiExceptionRecoveryAction.Terminate, UiExceptionRecoveryPolicy.Classify(wrappedBug));
    }

    [Fact]
    public void Classify_TerminatesOnAggregateWithSeveralInnerExceptions()
    {
        var aggregate = new AggregateException(new IOException(), new IOException());

        Assert.Equal(UiExceptionRecoveryAction.Terminate, UiExceptionRecoveryPolicy.Classify(aggregate));
    }
}
