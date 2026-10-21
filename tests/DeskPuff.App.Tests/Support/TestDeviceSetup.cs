using DeskPuff.App.ViewModels;

namespace DeskPuff.App.Tests;

internal static class TestDeviceSetup
{
    // Call only with an explicitly injected test client and test-owned storage.
    internal static async Task InitializeConnectedAsync(MainViewModel viewModel)
    {
        await viewModel.InitializeAsync();
        await viewModel.ConnectAsync(CancellationToken.None);
    }
}
