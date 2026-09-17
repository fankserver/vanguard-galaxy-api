using System;
using System.Threading;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class SkillTreeAndTooltipTests
{
    private static ShipModule Tractor(int automatic = 2, int manual = 3) =>
        new(ShipModuleKind.Tractor, "Tractor Unit", 1, new[] { new ModuleStatLine("Beams", "2") }, new TractorModule(automatic, manual));
    private static ItemInfo Item(string id = "scrap") => new(id, "Scrap", "A pile of scrap.", 12, 3);

    [Fact]
    public void SkillTreesAreIndependentOfEquipmentAndDescribeAnySpecialization()
    {
        using var service = new SkillTreeService(new LifecycleHub((_, _) => { }));
        Assert.Null(service.Get(CommanderSpecialization.Mining));
        int level = 4;
        service.Bind(s => new SkillTree(s.ToString(), s, level, 100));
        var original = service.Get(CommanderSpecialization.Mining)!;
        level = 10;
        Assert.Equal(4, original.MasteryLevel);
        Assert.Equal(10, service.Get(CommanderSpecialization.Mining)!.MasteryLevel);
        service.Bind(_ => null); // No commander/tree.
        Assert.Null(service.Get(CommanderSpecialization.Engineering));
        service.Dispose();
        Assert.Null(service.Get(CommanderSpecialization.Mining));
    }

    [Fact]
    public void TooltipCallbacksCanAddStyledTextAndDispatchByPayloadType()
    {
        using var service = new TooltipService(new LifecycleHub((_, _) => { }));
        service.SetAvailable(true);
        service.RegisterSkillTree("mod", (tree, tooltip) =>
            tooltip.AddLine("Mining " + tree.MasteryLevel, TooltipTextStyle.Bonus).Append(" (mod)", TooltipTextStyle.Details));
        service.RegisterShipModule("mod", (module, tooltip) =>
        {
            Assert.Equal("Beams", Assert.Single(module.StatLines).Label);
            tooltip.AddLine("Beams " + module.Tractor!.BeamCount);
        });
        service.RegisterItem("mod", (item, tooltip) => tooltip.AddLine("Item " + item.Identifier + " x" + item.Count));
        var skillLines = service.Describe(new SkillTree("Mining", CommanderSpecialization.Mining, 12, 100));
        Assert.Single(skillLines);
        Assert.Equal("Mining 12", skillLines[0].Spans[0].Text);
        Assert.Equal(TooltipTextStyle.Details, skillLines[0].Spans[1].Style);
        Assert.Equal("Beams 2", Assert.Single(service.Describe(Tractor())).Spans[0].Text);
        Assert.Equal("Item scrap x12", Assert.Single(service.Describe(Item())).Spans[0].Text);
        // Non-tractor modules receive their kind without a tractor snapshot.
        service.RegisterShipModule("other", (module, tooltip) =>
        { Assert.Equal(ShipModuleKind.ShieldGenerator, module.Kind); Assert.Null(module.Tractor); tooltip.AddLine("shielded"); });
        Assert.Equal("shielded", Assert.Single(service.Describe(new ShipModule(ShipModuleKind.ShieldGenerator, "Shield", 0, Array.Empty<ModuleStatLine>(), null))).Spans[0].Text);
    }

    [Fact]
    public void OneOwnerMayRegisterEveryTooltipFamilyOnce()
    {
        using var service = new TooltipService(new LifecycleHub((_, _) => { }));
        service.SetAvailable(true);
        service.RegisterShipModule("mod", (_, _) => { });
        service.RegisterItem("mod", (_, _) => { });
        service.RegisterSkillTree("mod", (_, _) => { });
        Assert.Throws<InvalidOperationException>(() => service.RegisterShipModule("mod", (_, _) => { }));
        Assert.Throws<InvalidOperationException>(() => service.RegisterItem("mod", (_, _) => { }));
    }

    [Fact]
    public void FaultyTooltipContributorDoesNotLeakPartialLinesOrSuppressOthers()
    {
        int errors = 0;
        using var service = new TooltipService(new LifecycleHub((_, _) => errors++));
        service.SetAvailable(true);
        Tooltip? retained = null; TooltipLine? retainedLine = null;
        service.RegisterItem("broken", (_, tooltip) =>
        {
            retained = tooltip; retainedLine = tooltip.AddLine("discard this");
            throw new InvalidOperationException();
        });
        service.RegisterItem("working", (_, tooltip) => tooltip.AddLine("keep this"));
        Assert.Equal("keep this", Assert.Single(service.Describe(Item())).Spans[0].Text);
        Assert.Equal(1, errors);
        Assert.Throws<InvalidOperationException>(() => retained!.AddLine("late"));
        Assert.Throws<InvalidOperationException>(() => retainedLine!.Append("late"));
    }

    [Fact]
    public void TooltipReentrancyAndDisposalAreSafe()
    {
        using var service = new TooltipService(new LifecycleHub((_, _) => { }));
        var module = Tractor();
        service.SetAvailable(true);
        var lease = service.RegisterShipModule("mod", (_, tooltip) =>
        {
            Assert.Empty(service.Describe(module));
            tooltip.AddLine("ok");
        });
        Assert.Single(service.Describe(module));
        lease.Dispose();
        Assert.Empty(service.Describe(module));
        service.Dispose();
        Assert.Throws<ObjectDisposedException>(() => service.RegisterShipModule("mod", (_, _) => { }));
    }

    [Fact]
    public void IndependentServicesRejectForeignThreads()
    {
        var hub = new LifecycleHub((_, _) => { });
        using var skills = new SkillTreeService(hub);
        using var equipment = new EquipmentService(hub);
        using var tooltips = new TooltipService(hub);
        Exception? failed = null;
        var worker = new Thread(() =>
        {
            try
            {
                Assert.Throws<InvalidOperationException>(() => skills.Get(CommanderSpecialization.Engineering));
                Assert.Throws<InvalidOperationException>(() => equipment.ConfigurePlayerTractorModules("mod", _ => null));
                Assert.Throws<InvalidOperationException>(() => tooltips.RegisterSkillTree("mod", (_, _) => { }));
                Assert.Throws<InvalidOperationException>(() => tooltips.RegisterShipModule("mod", (_, _) => { }));
                Assert.Throws<InvalidOperationException>(() => tooltips.RegisterItem("mod", (_, _) => { }));
            }
            catch (Exception error) { failed = error; }
        });
        worker.Start(); Assert.True(worker.Join(TimeSpan.FromSeconds(5))); Assert.Null(failed);
    }
}
