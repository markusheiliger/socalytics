using Shouldly;
using SocAlytics.Analysts.Manager.Diagnostics;
using SocAlytics.Analysts.Manager.Platform;
using SocAlytics.Analysts.Manager.Queue;
using SocAlytics.Analysts.Manager.Tests.Support;
using SocAlytics.Analysts.Manager.ViewModels;
using Xunit;

namespace SocAlytics.Analysts.Manager.Tests.Ui;

public sealed class StatusWindowQueueDepthTests
{
    [Fact]
    public void Waiting_work_follows_the_queue_depth_and_raises_on_change()
    {
        var control = new FakeManagerControl();
        var vm = new StatusWindowViewModel(control);
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        vm.WaitingWork.ShouldBe("Queue: no information yet");

        var at = new DateTimeOffset(2026, 10, 15, 19, 40, 0, TimeSpan.Zero);
        control.QueueDepth = new QueueDepthView(
            new QueueDepthSnapshot(QueueDepthStatus.Stale, at, at, [new QueueDepthCapability("person-detection", 5)], 5, "x"), 200);
        control.Raise();

        vm.WaitingWork.ShouldContain("stale");
        vm.WaitingWork.ShouldContain("person-detection: 5");
        raised.ShouldContain(nameof(StatusWindowViewModel.WaitingWork));
    }
}
