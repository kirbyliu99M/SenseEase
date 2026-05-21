using System.Collections.Specialized;
using System.Linq;
using SenseEase.App.ViewModels;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// Behaviour tests for the fixed-capacity ring buffer used by the chart history bindings.
/// Guards the FIFO semantics + INotifyCollectionChanged contract — both of which the
/// chart layer in MainWindow assumes.
/// </summary>
public class RingHistoryTests
{
    [Fact]
    public void Append_WithinCapacity_PreservesOldestToNewestOrder()
    {
        var h = new MainViewModel.RingHistory(5);
        h.Append(1); h.Append(2); h.Append(3);

        Assert.Equal(3, h.Count);
        Assert.Equal(new double[] { 1, 2, 3 }, h.ToArray());
        Assert.Equal(1, h[0]);
        Assert.Equal(3, h[2]);
    }

    [Fact]
    public void Append_PastCapacity_RollsOldestOut_AndSaturatesCount()
    {
        var h = new MainViewModel.RingHistory(3);
        h.Append(1); h.Append(2); h.Append(3); h.Append(4); h.Append(5);

        Assert.Equal(3, h.Count);
        // Oldest two (1, 2) should have rolled out; window is [3, 4, 5].
        Assert.Equal(new double[] { 3, 4, 5 }, h.ToArray());
    }

    [Fact]
    public void Indexer_AfterMultipleWraps_StaysOldestToNewest()
    {
        var h = new MainViewModel.RingHistory(3);
        for (int i = 1; i <= 100; i++) h.Append(i);

        // Window should be [98, 99, 100].
        Assert.Equal(98, h[0]);
        Assert.Equal(99, h[1]);
        Assert.Equal(100, h[2]);
    }

    [Fact]
    public void Enumerator_YieldsInOldestToNewestOrder()
    {
        var h = new MainViewModel.RingHistory(4);
        h.Append(10); h.Append(20); h.Append(30); h.Append(40); h.Append(50);

        Assert.Equal(new double[] { 20, 30, 40, 50 }, h.ToList());
    }

    [Fact]
    public void Append_RaisesCollectionChangedWithResetAction()
    {
        var h = new MainViewModel.RingHistory(3);
        int count = 0;
        NotifyCollectionChangedAction? lastAction = null;
        h.CollectionChanged += (_, args) => { count++; lastAction = args.Action; };

        h.Append(1);
        h.Append(2);

        Assert.Equal(2, count);
        // Reset is required by ItemsControl when items shift positions on roll-out.
        Assert.Equal(NotifyCollectionChangedAction.Reset, lastAction);
    }

    [Fact]
    public void EmptyRing_HasZeroCount_AndEnumeratesNothing()
    {
        var h = new MainViewModel.RingHistory(8);
        Assert.Empty(h);
        Assert.Empty(h.ToList());
    }
}
