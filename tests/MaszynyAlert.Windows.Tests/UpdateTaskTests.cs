using MaszynyAlert.Windows;

namespace MaszynyAlert.Windows.Tests;

public sealed class UpdateTaskTests
{
    [Fact]
    public void MissingTaskIsNotRegistered()
    {
        Assert.False(UpdateTask.IsRegistered("VNC Alert test " + Guid.NewGuid().ToString("N")));
    }
}
