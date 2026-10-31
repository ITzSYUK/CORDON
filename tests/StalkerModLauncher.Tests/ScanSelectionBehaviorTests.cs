using System.Windows.Controls;
using StalkerModLauncher.Infrastructure;
using Xunit;

namespace StalkerModLauncher.Tests;

public sealed class ScanSelectionBehaviorTests
{
    private static readonly int[] ExpectedSelection = [0, 1, 2, 3];

    [Theory]
    [InlineData(1, 3)]
    [InlineData(3, 1)]
    public void RangeAddsEveryItemAndKeepsExistingSelection(int anchor, int end)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var list = new ListView { SelectionMode = SelectionMode.Multiple };
                for (var i = 0; i < 5; i++) list.Items.Add(i);
                list.SelectedItems.Add(0);
                list.SelectedItems.Add(2);

                ScanSelectionBehavior.SelectRange(list, anchor, end);

                Assert.Equal(ExpectedSelection, list.SelectedItems.Cast<int>().OrderBy(i => i));
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
