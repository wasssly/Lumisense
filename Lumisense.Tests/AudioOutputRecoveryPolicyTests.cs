using AudioPlayer;
using Xunit;

namespace Lumisense.Tests;

public sealed class AudioOutputRecoveryPolicyTests
{
    [Fact]
    public void FollowsSystemDefault_RecognizesEmptyPersistedKey()
    {
        Assert.True(AudioOutputRecoveryPolicy.FollowsSystemDefault(null));
        Assert.True(AudioOutputRecoveryPolicy.FollowsSystemDefault(string.Empty));
        Assert.False(AudioOutputRecoveryPolicy.FollowsSystemDefault("wasapi:{endpoint-a}"));
    }

    [Fact]
    public void ShouldRecoverAfterDefaultDeviceChanged_OnlyRestartsForAnotherEndpoint()
    {
        Assert.False(AudioOutputRecoveryPolicy.ShouldRecoverAfterDefaultDeviceChanged(
            string.Empty, "{endpoint-a}", "{endpoint-a}"));
        Assert.True(AudioOutputRecoveryPolicy.ShouldRecoverAfterDefaultDeviceChanged(
            string.Empty, "{endpoint-a}", "{endpoint-b}"));
    }

    [Fact]
    public void ShouldRecoverAfterDefaultDeviceChanged_DoesNotOverrideExplicitDevice()
    {
        Assert.False(AudioOutputRecoveryPolicy.ShouldRecoverAfterDefaultDeviceChanged(
            "wasapi:{endpoint-a}", "{endpoint-a}", "{endpoint-b}"));
    }
}
