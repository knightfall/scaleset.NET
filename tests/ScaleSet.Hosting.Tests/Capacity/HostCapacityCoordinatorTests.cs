using System.Threading;
using ScaleSet.Hosting.Capacity;
using ScaleSet.Hosting.Configuration;
using ScaleSet.Hosting.Providers;
using ScaleSet.Hosting.Scaling;

namespace ScaleSet.Hosting.Tests.Capacity;

public sealed class HostCapacityCoordinatorTests
{
    [Fact]
    public void Plan_IntersectsAggregateCountCpuAndMemoryBudgets()
    {
        (HostCapacityLimits Limits, int Expected)[] cases =
        [
            (new HostCapacityLimits(1, null, null, 1), 1),
            (new HostCapacityLimits(null, 1.5m, null, 1), 1),
            (new HostCapacityLimits(null, null, 512, 1), 1),
            (new HostCapacityLimits(2, 2m, 1024, 1), 2),
        ];

        foreach ((HostCapacityLimits limits, int expected) in cases)
        {
            HostCapacityCoordinator coordinator = Coordinator(limits, Set("build", maximum: 4));
            CapacityPlan plan = coordinator.CreatePlan(new Dictionary<string, int> { ["build"] = 4 });
            ScaleSetCapacityPlan setPlan = plan.GetScaleSet("build");

            Assert.Equal(expected, setPlan.ReservableAdditionalRunners);
            Assert.Equal(expected, setPlan.AdvertisedMaximum);

            CapacityReservationBatchAttempt rejected = coordinator.TryReserveBatch(
                plan,
                Enumerable.Range(0, expected + 1).Select(index => Request("build", index)));
            Assert.Equal(CapacityReservationStatus.InsufficientCapacity, rejected.Status);
            Assert.Equal(0, coordinator.CaptureSnapshot().Usage.RunnerCount);

            CapacityReservationBatchAttempt reserved = coordinator.TryReserveBatch(
                plan,
                Enumerable.Range(0, expected).Select(index => Request("build", index)));
            Assert.Equal(CapacityReservationStatus.Reserved, reserved.Status);
            Assert.Equal(expected, coordinator.CaptureSnapshot().Usage.RunnerCount);
        }
    }

    [Fact]
    public async Task ConcurrentPlans_CannotOversubscribeSharedCapacity()
    {
        HostCapacityCoordinator coordinator = Coordinator(
            new HostCapacityLimits(1, null, null, 1),
            Set("build", maximum: 2));
        CapacityPlan plan = coordinator.CreatePlan(new Dictionary<string, int> { ["build"] = 2 });
        CapacityReservationRequest[] requests = [Request("build", 0), Request("build", 1)];
        using Barrier barrier = new(participantCount: requests.Length + 1);

        Task<CapacityReservationAttempt>[] attempts = requests
            .Select(request => Task.Run(() =>
            {
                barrier.SignalAndWait(TestContext.Current.CancellationToken);
                return coordinator.TryReserve(plan, request);
            }))
            .ToArray();
        barrier.SignalAndWait(TestContext.Current.CancellationToken);

        CapacityReservationAttempt[] results = await Task.WhenAll(attempts);

        Assert.Single(results, result => result.Status == CapacityReservationStatus.Reserved);
        Assert.Single(results, result => result.Status == CapacityReservationStatus.StalePlan);
        Assert.Equal(1, coordinator.CaptureSnapshot().Usage.RunnerCount);
    }

    [Fact]
    public void TryReserveBatch_RejectsPlanAfterConcurrentLedgerChange()
    {
        HostCapacityCoordinator coordinator = Coordinator(
            new HostCapacityLimits(2, null, null, 1),
            Set("build", maximum: 2));
        CapacityPlan plan = coordinator.CreatePlan(new Dictionary<string, int> { ["build"] = 2 });

        CapacityReservationAttempt first = coordinator.TryReserve(plan, Request("build", 0));
        CapacityReservationAttempt second = coordinator.TryReserve(plan, Request("build", 1));

        Assert.Equal(CapacityReservationStatus.Reserved, first.Status);
        Assert.Equal(CapacityReservationStatus.StalePlan, second.Status);
        Assert.Equal(1, coordinator.CaptureSnapshot().Usage.RunnerCount);
    }

    [Fact]
    public void TryReserve_SameOperationAndResourcesIsIdempotent()
    {
        HostCapacityCoordinator coordinator = Coordinator(
            new HostCapacityLimits(2, 2m, 1024, 1),
            Set("build", maximum: 2));
        CapacityPlan plan = coordinator.CreatePlan(new Dictionary<string, int> { ["build"] = 1 });
        Guid operationId = Guid.NewGuid();
        RunnerIdentity identity = Identity("build", operationId);
        CapacityReservationRequest request = Request(identity, cpu: 1m, memoryMiB: 512);

        CapacityReservationAttempt first = coordinator.TryReserve(plan, request);
        CapacityReservationAttempt repeated = coordinator.TryReserve(plan, request);
        CapacityReservationAttempt enriched = coordinator.TryReserve(
            plan,
            Request(Identity("build", operationId, githubRunnerId: 123), cpu: 1m, memoryMiB: 512));

        Assert.Equal(CapacityReservationStatus.Reserved, first.Status);
        Assert.Equal(CapacityReservationStatus.AlreadyReserved, repeated.Status);
        Assert.Equal(CapacityReservationStatus.AlreadyReserved, enriched.Status);
        Assert.Equal(first.Handle!.Generation, repeated.Handle!.Generation);
        Assert.Equal(first.Handle.Generation, enriched.Handle!.Generation);
        Assert.Equal(1, coordinator.CaptureSnapshot().Usage.RunnerCount);
    }

    [Fact]
    public void TryReserve_RejectsOperationIdentityOrResourceConflicts()
    {
        HostCapacityCoordinator coordinator = Coordinator(
            new HostCapacityLimits(4, null, null, 1),
            Set("build", maximum: 4),
            Set("test", maximum: 4));
        Guid operationId = Guid.NewGuid();
        CapacityReservationRequest original = Request(Identity("build", operationId), cpu: 1m, memoryMiB: 512);
        CapacityPlan plan = coordinator.CreatePlan(new Dictionary<string, int> { ["build"] = 3, ["test"] = 3 });
        CapacityReservationAttempt first = coordinator.TryReserve(plan, original);

        CapacityReservationAttempt differentRunner = coordinator.TryReserve(
            plan,
            Request(Identity("build", operationId, runnerName: "another-runner"), cpu: 1m, memoryMiB: 512));
        CapacityReservationAttempt differentProfile = coordinator.TryReserve(
            plan,
            Request(Identity("build", operationId), cpu: 2m, memoryMiB: 512));
        CapacityReservationAttempt reusedOperationId = coordinator.TryReserve(
            plan,
            Request(Identity("test", operationId), cpu: 1m, memoryMiB: 512));
        CapacityReservationAttempt differentController = coordinator.TryReserve(
            plan,
            Request(Identity("build", operationId, controllerId: "another-controller"), cpu: 1m, memoryMiB: 512));
        CapacityReservationAttempt differentServerScaleSet = coordinator.TryReserve(
            plan,
            Request(Identity("build", operationId, scaleSetId: 99), cpu: 1m, memoryMiB: 512));

        Assert.Equal(CapacityReservationStatus.Reserved, first.Status);
        Assert.Equal(CapacityReservationStatus.IdentityConflict, differentRunner.Status);
        Assert.Equal(CapacityReservationStatus.IdentityConflict, differentProfile.Status);
        Assert.Equal(CapacityReservationStatus.IdentityConflict, reusedOperationId.Status);
        Assert.Equal(CapacityReservationStatus.IdentityConflict, differentController.Status);
        Assert.Equal(CapacityReservationStatus.IdentityConflict, differentServerScaleSet.Status);
        Assert.Equal(1, coordinator.CaptureSnapshot().Usage.RunnerCount);
    }

    [Fact]
    public void TryRelease_UsesRevisionAndGenerationGuards()
    {
        HostCapacityCoordinator coordinator = Coordinator(
            new HostCapacityLimits(3, null, null, 1),
            Set("build", maximum: 3));
        CapacityReservationAttempt reserved = coordinator.TryReserve(
            coordinator.CreatePlan(new Dictionary<string, int> { ["build"] = 1 }),
            Request("build", 0));
        CapacityReservationHandle originalHandle = reserved.Handle!;

        CapacityStateUpdateAttempt update = coordinator.TryUpdateState(
            originalHandle,
            reserved.Revision,
            CapacityOccupancyState.Creating);
        CapacityReleaseStatus staleRevision = coordinator.TryRelease(originalHandle, reserved.Revision);
        CapacityReleaseStatus released = coordinator.TryRelease(originalHandle, update.CurrentRevision);
        CapacityReleaseStatus duplicateRelease = coordinator.TryRelease(originalHandle, update.CurrentRevision);

        CapacityInventoryAdoptionAttempt readopted = coordinator.AdoptInventory(
            new CapacityInventoryItem(originalHandle.Identity, originalHandle.Resources, CapacityOccupancyState.Unknown));
        CapacityReleaseStatus staleGeneration = coordinator.TryRelease(originalHandle, update.CurrentRevision);

        Assert.Equal(CapacityStateUpdateStatus.Updated, update.Status);
        Assert.Equal(CapacityReleaseStatus.StaleRevision, staleRevision);
        Assert.Equal(CapacityReleaseStatus.Released, released);
        Assert.Equal(CapacityReleaseStatus.AlreadyReleased, duplicateRelease);
        Assert.Equal(CapacityInventoryAdoptionStatus.Adopted, readopted.Status);
        Assert.True(readopted.Handle!.Generation > originalHandle.Generation);
        Assert.Equal(CapacityReleaseStatus.StaleHandle, staleGeneration);
        Assert.Equal(1, coordinator.CaptureSnapshot().Usage.RunnerCount);
    }

    [Fact]
    public void AdoptInventory_CountsEveryProtectedLifecycleStage()
    {
        HostCapacityCoordinator coordinator = Coordinator(
            new HostCapacityLimits(null, null, null, 2),
            Set("build", maximum: 10));
        CapacityOccupancyState[] states = Enum.GetValues<CapacityOccupancyState>();

        foreach (CapacityOccupancyState state in states)
        {
            CapacityInventoryAdoptionAttempt adopted = coordinator.AdoptInventory(
                new CapacityInventoryItem(
                    Identity("build", Guid.NewGuid(), runnerName: $"runner-{state}"),
                    new CapacityResourceQuantities(1m, 512),
                    state));
            Assert.Equal(CapacityInventoryAdoptionStatus.Adopted, adopted.Status);
        }

        HostCapacitySnapshot snapshot = coordinator.CaptureSnapshot();
        Assert.Equal(states.Length, snapshot.Usage.RunnerCount);
        foreach (CapacityOccupancyState state in states)
        {
            Assert.Equal(1, snapshot.Usage.StateCounts[state]);
        }
    }

    [Fact]
    public void UnknownInventory_ConsumesConfiguredCpuAndMemoryHeadroom()
    {
        foreach ((CapacityResourceQuantities Unknown, bool CpuBlocked) in new[]
        {
            (new CapacityResourceQuantities(null, 512), true),
            (new CapacityResourceQuantities(1m, null), false),
        })
        {
            HostCapacityCoordinator coordinator = Coordinator(
                new HostCapacityLimits(8, 4m, 2048, 1),
                Set("build", maximum: 8));
            coordinator.AdoptInventory(new CapacityInventoryItem(
                Identity("build", Guid.NewGuid()),
                Unknown,
                CapacityOccupancyState.Unknown));

            CapacityPlan plan = coordinator.CreatePlan(new Dictionary<string, int> { ["build"] = 8 });
            CapacityUsageSnapshot usage = coordinator.CaptureSnapshot().Usage;

            Assert.Equal(0, plan.GetScaleSet("build").ReservableAdditionalRunners);
            Assert.Equal(CpuBlocked ? 1 : 0, usage.UnknownCpuCount);
            Assert.Equal(CpuBlocked ? 0 : 1, usage.UnknownMemoryCount);
        }
    }

    [Fact]
    public void AdoptInventory_IsIdempotentAndResourceConflictsBecomeUnknown()
    {
        HostCapacityCoordinator coordinator = Coordinator(
            new HostCapacityLimits(4, 4m, 2048, 1),
            Set("build", maximum: 4));
        RunnerIdentity identity = Identity("build", Guid.NewGuid());
        CapacityInventoryItem original = new(
            identity,
            new CapacityResourceQuantities(1m, 512),
            CapacityOccupancyState.Busy);

        CapacityInventoryAdoptionAttempt first = coordinator.AdoptInventory(original);
        CapacityInventoryAdoptionAttempt repeated = coordinator.AdoptInventory(original);
        CapacityInventoryAdoptionAttempt conflict = coordinator.AdoptInventory(new CapacityInventoryItem(
            identity,
            new CapacityResourceQuantities(2m, 512),
            CapacityOccupancyState.Busy));

        Assert.Equal(CapacityInventoryAdoptionStatus.Adopted, first.Status);
        Assert.Equal(CapacityInventoryAdoptionStatus.AlreadyAdopted, repeated.Status);
        Assert.Equal(CapacityInventoryAdoptionStatus.ResourceConflict, conflict.Status);
        Assert.Equal(first.Handle!.Generation, repeated.Handle!.Generation);
        Assert.Null(conflict.Handle!.Resources.CpuCores);
        Assert.Equal(CapacityReleaseStatus.StaleHandle, coordinator.TryRelease(first.Handle, first.Revision));
        Assert.Equal(1, coordinator.CaptureSnapshot().Usage.RunnerCount);
        Assert.Equal(1, coordinator.CaptureSnapshot().Usage.UnknownCpuCount);
    }

    [Fact]
    public void Plan_AfterBudgetReductionPreservesInventoryWithZeroHeadroom()
    {
        HostCapacityCoordinator coordinator = Coordinator(
            new HostCapacityLimits(1, 2m, 1024, 1),
            Set("build", maximum: 4));
        foreach (CapacityOccupancyState state in new[] { CapacityOccupancyState.Busy, CapacityOccupancyState.Unknown })
        {
            coordinator.AdoptInventory(new CapacityInventoryItem(
                Identity("build", Guid.NewGuid()),
                new CapacityResourceQuantities(1m, 512),
                state));
        }

        CapacityPlan plan = coordinator.CreatePlan(new Dictionary<string, int> { ["build"] = 4 });
        HostCapacitySnapshot snapshot = coordinator.CaptureSnapshot();

        Assert.Equal(2, snapshot.Usage.RunnerCount);
        Assert.Equal(1, snapshot.Usage.StateCounts[CapacityOccupancyState.Busy]);
        Assert.Equal(1, snapshot.Usage.StateCounts[CapacityOccupancyState.Unknown]);
        Assert.Equal(0, plan.GetScaleSet("build").ReservableAdditionalRunners);
        Assert.Equal(0, plan.GetScaleSet("build").AdvertisedMaximum);
    }

    [Fact]
    public void Plan_RoundRobinsFeasibleCapacityAcrossScaleSets()
    {
        HostCapacityCoordinator coordinator = Coordinator(
            new HostCapacityLimits(4, null, null, 1),
            Set("alpha", maximum: 10),
            Set("beta", maximum: 10));
        coordinator.AdoptInventory(new CapacityInventoryItem(
            Identity("alpha", Guid.NewGuid()),
            new CapacityResourceQuantities(1m, 512),
            CapacityOccupancyState.Busy));
        Dictionary<string, int> demand = new(StringComparer.OrdinalIgnoreCase)
        {
            ["alpha"] = 10,
            ["beta"] = 10,
        };

        CapacityPlan firstPlan = coordinator.CreatePlan(demand);
        ScaleSetCapacityPlan alpha = firstPlan.GetScaleSet("alpha");
        ScaleSetCapacityPlan beta = firstPlan.GetScaleSet("beta");

        Assert.Equal(2, alpha.ReservableAdditionalRunners);
        Assert.Equal(1, beta.ReservableAdditionalRunners);
        Assert.Equal(2, alpha.AdvertisedMaximum);
        Assert.Equal(2, beta.AdvertisedMaximum);
        Assert.Equal(4, firstPlan.ScaleSets.Values.Sum(static item => item.AdvertisedMaximum));

        CapacityReservationAttempt reservedAlpha = coordinator.TryReserve(firstPlan, Request("alpha", 0));
        CapacityPlan nextPlan = coordinator.CreatePlan(demand);

        Assert.Equal(CapacityReservationStatus.Reserved, reservedAlpha.Status);
        Assert.Equal(1, nextPlan.GetScaleSet("beta").ReservableAdditionalRunners);
        Assert.Equal(1, nextPlan.GetScaleSet("alpha").ReservableAdditionalRunners);
    }

    [Fact]
    public void Plan_UsesConfiguredScalingPolicyForDesiredCapacity()
    {
        HostCapacityCoordinator coordinator = Coordinator(
            new HostCapacityLimits(20, null, null, 2),
            Set("spare", policy: ScalingPolicy.SpareCapacity, minimum: 2, maximum: 10),
            Set("floor", policy: ScalingPolicy.TotalFloor, minimum: 2, maximum: 10));

        CapacityPlan plan = coordinator.CreatePlan(new Dictionary<string, int>
        {
            ["spare"] = 3,
            ["floor"] = 3,
        });

        Assert.Equal(5, plan.GetScaleSet("spare").DesiredTotalRunners);
        Assert.Equal(3, plan.GetScaleSet("floor").DesiredTotalRunners);
    }

    [Fact]
    public void Plan_AdvertisesFeasibleHeadroomBeforeFirstAssignedJobObservation()
    {
        HostCapacityCoordinator coordinator = Coordinator(
            new HostCapacityLimits(3, 3m, 1536, 1),
            Set("alpha", maximum: 10),
            Set("beta", maximum: 10));

        CapacityPlan plan = coordinator.CreatePlan();

        Assert.Equal(0, plan.GetScaleSet("alpha").DesiredTotalRunners);
        Assert.Equal(0, plan.GetScaleSet("beta").DesiredTotalRunners);
        Assert.Equal(2, plan.GetScaleSet("alpha").ReservableAdditionalRunners);
        Assert.Equal(1, plan.GetScaleSet("beta").ReservableAdditionalRunners);
        Assert.Equal(3, plan.ScaleSets.Values.Sum(static item => item.AdvertisedMaximum));

        CapacityReservationBatchAttempt reserved = coordinator.TryReserveBatch(plan,
        [
            Request("alpha", 0),
            Request("alpha", 1),
            Request("beta", 0),
        ]);

        Assert.Equal(CapacityReservationStatus.Reserved, reserved.Status);
        Assert.Equal(3, coordinator.CaptureSnapshot().Usage.RunnerCount);
    }

    [Fact]
    public void Plan_UnknownOccupancyDoesNotCreateSlotsButAssignedJobsRemainAdvertised()
    {
        HostCapacityCoordinator coordinator = Coordinator(
            new HostCapacityLimits(8, null, null, 1),
            Set("build", maximum: 10));
        for (int index = 0; index < 8; index++)
        {
            coordinator.AdoptInventory(new CapacityInventoryItem(
                Identity("build", Guid.NewGuid()),
                new CapacityResourceQuantities(1m, 512),
                CapacityOccupancyState.Unknown));
        }

        CapacityPlan noAssignedJobs = coordinator.CreatePlan(new Dictionary<string, int> { ["build"] = 0 });
        CapacityPlan assignedJobs = coordinator.CreatePlan(new Dictionary<string, int> { ["build"] = 5 });

        Assert.Equal(0, noAssignedJobs.GetScaleSet("build").AdvertisedMaximum);
        Assert.Equal(0, noAssignedJobs.GetScaleSet("build").ReservableAdditionalRunners);
        Assert.Equal(5, assignedJobs.GetScaleSet("build").AdvertisedMaximum);
        Assert.Equal(0, assignedJobs.GetScaleSet("build").ReservableAdditionalRunners);
        Assert.Equal(8, coordinator.CaptureSnapshot().Usage.RunnerCount);
    }

    [Fact]
    public void Plan_KnownIdleAndStartingCapacityAddsSlotsWhileBusyRemovingAndFailedDoNot()
    {
        HostCapacityCoordinator coordinator = Coordinator(
            new HostCapacityLimits(8, null, null, 1),
            Set("build", maximum: 10));
        CapacityOccupancyState[] states =
        [
            CapacityOccupancyState.Idle,
            CapacityOccupancyState.Starting,
            CapacityOccupancyState.Busy,
            CapacityOccupancyState.Unknown,
            CapacityOccupancyState.Removing,
            CapacityOccupancyState.Failed,
            CapacityOccupancyState.Busy,
            CapacityOccupancyState.Unknown,
        ];
        foreach (CapacityOccupancyState state in states)
        {
            coordinator.AdoptInventory(new CapacityInventoryItem(
                Identity("build", Guid.NewGuid(), runnerName: $"runner-{state}-{Guid.NewGuid():N}"),
                new CapacityResourceQuantities(1m, 512),
                state));
        }

        CapacityPlan plan = coordinator.CreatePlan(new Dictionary<string, int> { ["build"] = 5 });

        Assert.Equal(7, plan.GetScaleSet("build").AdvertisedMaximum);
        Assert.Equal(0, plan.GetScaleSet("build").ReservableAdditionalRunners);
    }

    [Fact]
    public void Plan_GlobalZeroOrExceededHostBudgetBlocksQueueAcquisition()
    {
        HostCapacityCoordinator globallyPaused = Coordinator(
            new HostCapacityLimits(0, null, null, 1),
            Set("build", maximum: 10));
        globallyPaused.AdoptInventory(new CapacityInventoryItem(
            Identity("build", Guid.NewGuid()),
            new CapacityResourceQuantities(1m, 512),
            CapacityOccupancyState.Idle));

        HostCapacityCoordinator overLimit = Coordinator(
            new HostCapacityLimits(7, null, null, 1),
            Set("build", maximum: 10));
        for (int index = 0; index < 8; index++)
        {
            overLimit.AdoptInventory(new CapacityInventoryItem(
                Identity("build", Guid.NewGuid()),
                new CapacityResourceQuantities(1m, 512),
                CapacityOccupancyState.Unknown));
        }

        HostCapacityCoordinator cpuOverLimit = Coordinator(
            new HostCapacityLimits(null, 1m, null, 1),
            Set("build", maximum: 10));
        cpuOverLimit.AdoptInventory(new CapacityInventoryItem(
            Identity("build", Guid.NewGuid()),
            new CapacityResourceQuantities(2m, 512),
            CapacityOccupancyState.Unknown));

        HostCapacityCoordinator memoryOverLimit = Coordinator(
            new HostCapacityLimits(null, null, 512, 1),
            Set("build", maximum: 10));
        memoryOverLimit.AdoptInventory(new CapacityInventoryItem(
            Identity("build", Guid.NewGuid()),
            new CapacityResourceQuantities(1m, 1024),
            CapacityOccupancyState.Unknown));

        CapacityPlan zeroLimitPlan = globallyPaused.CreatePlan(new Dictionary<string, int> { ["build"] = 5 });
        CapacityPlan exceededPlan = overLimit.CreatePlan(new Dictionary<string, int> { ["build"] = 5 });
        CapacityPlan cpuOverLimitPlan = cpuOverLimit.CreatePlan(new Dictionary<string, int> { ["build"] = 1 });
        CapacityPlan memoryOverLimitPlan = memoryOverLimit.CreatePlan(new Dictionary<string, int> { ["build"] = 1 });

        Assert.Equal(0, zeroLimitPlan.GetScaleSet("build").AdvertisedMaximum);
        Assert.Equal(0, zeroLimitPlan.GetScaleSet("build").ReservableAdditionalRunners);
        Assert.Equal(0, exceededPlan.GetScaleSet("build").AdvertisedMaximum);
        Assert.Equal(0, cpuOverLimitPlan.GetScaleSet("build").AdvertisedMaximum);
        Assert.Equal(0, memoryOverLimitPlan.GetScaleSet("build").AdvertisedMaximum);
        Assert.Equal(8, overLimit.CaptureSnapshot().Usage.RunnerCount);
    }

    [Fact]
    public void Plan_MaximumZeroAdvertisesZeroAndRejectsNewReservations()
    {
        HostCapacityCoordinator coordinator = Coordinator(
            new HostCapacityLimits(10, null, null, 1),
            Set("paused", minimum: 0, maximum: 0));
        coordinator.AdoptInventory(new CapacityInventoryItem(
            Identity("paused", Guid.NewGuid()),
            new CapacityResourceQuantities(1m, 512),
            CapacityOccupancyState.Busy));

        CapacityPlan plan = coordinator.CreatePlan(new Dictionary<string, int> { ["paused"] = 10 });
        CapacityReservationAttempt reservation = coordinator.TryReserve(plan, Request("paused", 0));

        Assert.Equal(0, plan.GetScaleSet("paused").AdvertisedMaximum);
        Assert.Equal(0, plan.GetScaleSet("paused").ReservableAdditionalRunners);
        Assert.Equal(CapacityReservationStatus.InsufficientCapacity, reservation.Status);
        Assert.Equal(1, coordinator.CaptureSnapshot().Usage.RunnerCount);
        Assert.Equal(1, coordinator.CaptureSnapshot().Usage.StateCounts[CapacityOccupancyState.Busy]);
    }

    [Fact]
    public void Plan_LargeCountUsesBulkRoundsAndReturnsExactFeasibleMaximum()
    {
        HostCapacityCoordinator coordinator = Coordinator(
            new HostCapacityLimits(null, null, null, 1),
            Set("large", maximum: int.MaxValue));

        CapacityPlan plan = coordinator.CreatePlan(new Dictionary<string, int> { ["large"] = int.MaxValue });
        ScaleSetCapacityPlan capacity = plan.GetScaleSet("large");

        Assert.Equal(int.MaxValue, capacity.DesiredTotalRunners);
        Assert.Equal(int.MaxValue, capacity.ReservableAdditionalRunners);
        Assert.Equal(int.MaxValue, capacity.AdvertisedMaximum);
        Assert.Equal(0, coordinator.CaptureSnapshot().Usage.RunnerCount);
    }

    [Fact]
    public void HostCapacityCoordinator_MapsAcceptedConfigurationOptions()
    {
        HostCapacityOptions hostCapacity = new()
        {
            MaxTotalRunners = 2,
            CpuBudget = 2m,
            MemoryBudget = 1024,
            CreationConcurrency = 1,
        };
        ScaleSetOptions scaleSet = new()
        {
            Key = "build",
            Scaling = new ScalingOptions
            {
                Policy = ScalingPolicy.TotalFloor,
                MinRunners = 0,
                MaxRunners = 2,
            },
            RunnerProfile = new RunnerProfileOptions
            {
                Cpu = 1m,
                Memory = 512,
            },
        };
        HostCapacityCoordinator coordinator = new(hostCapacity, new[] { scaleSet });

        CapacityPlan plan = coordinator.CreatePlan(new Dictionary<string, int> { ["build"] = 2 });

        Assert.Equal(2, plan.GetScaleSet("build").ReservableAdditionalRunners);
        Assert.Equal(1, coordinator.CreationConcurrencyGate.MaximumLeases);
    }

    [Fact]
    public void AdoptInventory_OverflowSaturatesAccountingAndBlocksFurtherAllocation()
    {
        HostCapacityCoordinator coordinator = Coordinator(
            new HostCapacityLimits(10, decimal.MaxValue, long.MaxValue, 1),
            Set("build", maximum: 10));
        CapacityResourceQuantities largest = new(decimal.MaxValue, long.MaxValue);
        coordinator.AdoptInventory(new CapacityInventoryItem(
            Identity("build", Guid.NewGuid()), largest, CapacityOccupancyState.Busy));
        coordinator.AdoptInventory(new CapacityInventoryItem(
            Identity("build", Guid.NewGuid()), largest, CapacityOccupancyState.Unknown));

        HostCapacitySnapshot snapshot = coordinator.CaptureSnapshot();
        CapacityPlan plan = coordinator.CreatePlan(new Dictionary<string, int> { ["build"] = 10 });

        Assert.Equal(2, snapshot.Usage.RunnerCount);
        Assert.True(snapshot.Usage.CpuOverflowed);
        Assert.True(snapshot.Usage.MemoryOverflowed);
        Assert.Null(snapshot.Usage.CpuCoresUsed);
        Assert.Null(snapshot.Usage.MemoryMiBUsed);
        Assert.Equal(0, plan.GetScaleSet("build").ReservableAdditionalRunners);
    }

    [Fact]
    public void CapacityResourceQuantities_RejectsNegativeValues()
    {
        ArgumentOutOfRangeException cpu = Assert.Throws<ArgumentOutOfRangeException>(
            () => new CapacityResourceQuantities(-1m, 0));
        ArgumentOutOfRangeException memory = Assert.Throws<ArgumentOutOfRangeException>(
            () => new CapacityResourceQuantities(0m, -1));

        Assert.Equal("cpuCores", cpu.ParamName);
        Assert.Equal("memoryMiB", memory.ParamName);
    }

    [Fact]
    public void HostCapacityLimits_RejectNegativeOrZeroOptionalBudgetsAndConcurrency()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HostCapacityLimits(-1, null, null, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HostCapacityLimits(null, 0m, null, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HostCapacityLimits(null, null, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HostCapacityLimits(null, null, null, 0));
    }

    private static HostCapacityCoordinator Coordinator(
        HostCapacityLimits limits,
        params ScaleSetCapacityDefinition[] scaleSets) =>
        new(limits, scaleSets);

    private static ScaleSetCapacityDefinition Set(
        string key,
        ScalingPolicy policy = ScalingPolicy.TotalFloor,
        int minimum = 0,
        int maximum = 10,
        decimal cpu = 1m,
        long memoryMiB = 512) =>
        new(key, policy, minimum, maximum, new CapacityResourceQuantities(cpu, memoryMiB));

    private static CapacityReservationRequest Request(
        string scaleSetKey,
        int sequence,
        decimal cpu = 1m,
        long memoryMiB = 512) =>
        Request(Identity(scaleSetKey, Guid.NewGuid(), runnerName: $"{scaleSetKey}-request-{sequence}"), cpu, memoryMiB);

    private static CapacityReservationRequest Request(
        RunnerIdentity identity,
        decimal cpu = 1m,
        long memoryMiB = 512) =>
        new(identity, new CapacityResourceQuantities(cpu, memoryMiB));

    private static RunnerIdentity Identity(
        string scaleSetKey,
        Guid operationId,
        string? runnerName = null,
        long? githubRunnerId = null,
        string controllerId = "controller",
        long scaleSetId = 42) =>
        new(
            new RunnerOwnershipScope(controllerId, scaleSetKey, scaleSetId),
            operationId,
            runnerName ?? $"{scaleSetKey}-{operationId:N}",
            githubRunnerId);
}
